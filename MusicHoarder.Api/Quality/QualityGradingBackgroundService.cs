using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.RateLimiting;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Consumes <see cref="QualityGradingChannel"/> with bounded concurrency + a shared request-rate
/// gate, and (when enabled) periodically sweeps for enriched-but-ungraded/stale songs and enqueues
/// them — the "automatic grading stage". Manual grading enqueues here too, so both paths share the
/// same workers and rate limit.
/// </summary>
public class QualityGradingBackgroundService(
    IServiceScopeFactory scopeFactory,
    QualityGradingChannel channel,
    QualityGradingProgressTracker progressTracker,
    IQualityGradingService gradingService,
    IRuntimeSettingsService runtimeSettings,
    IOwnerLookupService ownerLookup,
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<QualityGradingBackgroundService> logger) : BackgroundService
{
    private static readonly EnrichmentStatus[] GradeableStatuses =
        [EnrichmentStatus.Matched, EnrichmentStatus.NeedsReview];

    // Own gate: the album grader holds its own too, so the two do not share one budget.
    private readonly RequestRateGate _rateGate = new();
    private int _warnedNotConfigured;

    // Songs that just failed to grade. A failure persists no grade row, so without this backoff the
    // auto-sweep re-enqueues them every sweep.
    private readonly FailureBackoffTracker _failureBackoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;
        logger.LogInformation(
            "Quality grading service started. Configured={Configured} Auto={Auto} Concurrency={Concurrency}",
            opts.IsConfigured, opts.AutoGradeAfterEnrichment, opts.Concurrency);

        var workers = Enumerable.Range(0, Math.Max(1, opts.Concurrency))
            .Select(i => RunWorkerAsync(i, stoppingToken))
            .ToArray();

        var sweep = RunAutoSweepLoopAsync(stoppingToken);

        await Task.WhenAll([sweep, .. workers]);
    }

    private Task RunAutoSweepLoopAsync(CancellationToken ct) =>
        IdleBackoffSweepLoop.RunAsync(
            // Small initial delay so the enrichment backfill has a head start before we sweep.
            initialDelay: TimeSpan.FromSeconds(10),
            baseIdleSeconds: () => options.CurrentValue.IdleDelaySeconds,
            maxIdleSeconds: 300,
            sweep: async token =>
            {
                var opts = options.CurrentValue;
                var enabled = (await runtimeSettings.GetAsync(token).ConfigureAwait(false)).QualityGradingEnabled;
                return enabled && opts.IsConfigured && opts.AutoGradeAfterEnrichment
                    && await EnqueueUngradedAsync(opts, token) > 0;
            },
            onSweepFailed: ex => logger.LogWarning(ex, "Quality auto-grade sweep failed"),
            ct);

    /// <summary>Finds gradeable songs whose latest grade is missing or stale and enqueues them. Returns the count enqueued.</summary>
    internal async Task<int> EnqueueUngradedAsync(QualityGradingOptions opts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var candidates = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            // Exclude demo rows: the read-only demo library is never auto-graded.
            .ExcludingDemoTenant()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .Where(s => GradeableStatuses.Contains(s.EnrichmentStatus))
            .OrderByDescending(s => s.EnrichedAtUtc)
            .Take(opts.BatchSize)
            .Select(s => new { s.Id, s.EnrichedAtUtc })
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var ids = candidates.Select(c => c.Id).ToList();

        // Latest grade per candidate song, in one query.
        var latestByGrade = await db.SongQualityGrades
            .IgnoreQueryFilters()
            .Where(g => ids.Contains(g.SongId))
            .GroupBy(g => g.SongId)
            .Select(grp => grp.OrderByDescending(g => g.GradedAtUtc).First())
            .ToListAsync(ct);

        var latest = latestByGrade.ToDictionary(g => g.SongId);

        // Drop songs still inside their post-failure backoff, and prune entries that have expired.
        var now = DateTime.UtcNow;
        _failureBackoff.Prune(now);

        var needsGrading = candidates.Where(c =>
        {
            if (_failureBackoff.IsBackingOff(c.Id, now)) return false;           // backing off
            if (!latest.TryGetValue(c.Id, out var g)) return true;            // never graded
            if (c.EnrichedAtUtc is { } e && g.GradedAtUtc < e) return true;  // re-enriched since
            // A prompt-version or model change is NOT auto-regraded here: it would re-grade the whole
            // library on every config bump. Such grades are surfaced as "outdated" in the API and
            // regraded only on an explicit manual / "regrade outdated" action (force:false still lets
            // the grader itself detect the version/model mismatch). See QualityEndpoints grade-outdated.
            return false;
        }).Select(c => c.Id).ToList();

        if (needsGrading.Count > 0)
        {
            channel.EnqueueRange(needsGrading, force: false);
            logger.LogInformation("Auto-grade sweep enqueued {Count} songs", needsGrading.Count);
        }

        return needsGrading.Count;
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            var runCompleted = false;
            try
            {
                if (ct.IsCancellationRequested) break;

                await _rateGate.WaitAsync(options.CurrentValue.RequestsPerSecond, ct);
                var result = await gradingService.GradeSongAsync(item.SongId, item.Force, ct);

                switch (result.Outcome)
                {
                    case GradeOutcome.Graded:
                        Interlocked.Exchange(ref _warnedNotConfigured, 0);
                        _failureBackoff.Clear(item.SongId); // recovered — clear any backoff
                        progressTracker.IncrementGraded();
                        break;
                    case GradeOutcome.NotConfigured:
                        // Throttle: warn once per "not configured" streak so logs explain the no-op
                        // (the queue can hold hundreds of items) without flooding.
                        if (Interlocked.Exchange(ref _warnedNotConfigured, 1) == 0)
                            logger.LogWarning(
                                "Quality grading is enqueued but not configured — skipping. Set QualityGrading:ApiKey (env QualityGrading__ApiKey) to enable grading.");
                        progressTracker.IncrementSkipped();
                        break;
                    case GradeOutcome.Skipped:
                    case GradeOutcome.NotFound:
                        progressTracker.IncrementSkipped();
                        break;
                    default:
                        BackOff(item.SongId);
                        progressTracker.RecordError(result.ErrorCode ?? "error", result.Error);
                        progressTracker.IncrementFailed();
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                BackOff(item.SongId);
                logger.LogWarning(ex, "Quality grading worker {WorkerId} failed on song {SongId}", workerId, item.SongId);
                progressTracker.RecordError("error", ex.Message);
                progressTracker.IncrementFailed();
            }
            finally
            {
                runCompleted = channel.MarkProcessed(item.SongId);
            }

            // The call that drained the last in-flight item closes the grading run — capture a
            // timeline snapshot so fresh AI scores land on the performance timeline.
            if (runCompleted)
                await CaptureGradingSnapshotAsync(ct);
        }
    }

    private async Task CaptureGradingSnapshotAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var snapshots = scope.ServiceProvider.GetRequiredService<IEnrichmentSnapshotService>();
            await snapshots.CaptureAsync(ownerLookup.OwnerUserId, SnapshotTrigger.GradingRun, "ai grading", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down — skip
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to capture grading snapshot");
        }
    }

    /// <summary>Marks a song as recently-failed so the auto-sweep skips it for the backoff window.</summary>
    private void BackOff(int songId) =>
        _failureBackoff.MarkFailed(songId, TimeSpan.FromSeconds(options.CurrentValue.FailureBackoffSeconds));
}
