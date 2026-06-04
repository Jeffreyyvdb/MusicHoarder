using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
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

    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTime _nextSlotUtc = DateTime.MinValue;
    private int _warnedNotConfigured;

    // Songs that just failed to grade, with the UTC instant they become eligible again. A failure
    // persists no grade row, so without this backoff the auto-sweep re-enqueues them every sweep.
    private readonly ConcurrentDictionary<int, DateTime> _failedUntil = new();

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

    private async Task RunAutoSweepLoopAsync(CancellationToken ct)
    {
        // Small initial delay so the enrichment backfill has a head start before we sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            try
            {
                var enabled = (await runtimeSettings.GetAsync(ct).ConfigureAwait(false)).QualityGradingEnabled;
                if (enabled && opts.IsConfigured && opts.AutoGradeAfterEnrichment)
                    await EnqueueUngradedAsync(opts, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Quality auto-grade sweep failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(opts.IdleDelaySeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Finds gradeable songs whose latest grade is missing or stale and enqueues them.</summary>
    internal async Task EnqueueUngradedAsync(QualityGradingOptions opts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var candidates = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            // Exclude demo rows: the read-only demo library is never auto-graded.
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate
                && s.OwnerUserId != WellKnownUsers.DemoId)
            .Where(s => GradeableStatuses.Contains(s.EnrichmentStatus))
            .OrderByDescending(s => s.EnrichedAtUtc)
            .Take(opts.BatchSize)
            .Select(s => new { s.Id, s.EnrichedAtUtc })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

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
        foreach (var kvp in _failedUntil)
            if (kvp.Value <= now)
                _failedUntil.TryRemove(kvp.Key, out _);

        var needsGrading = candidates.Where(c =>
        {
            if (_failedUntil.TryGetValue(c.Id, out var until) && until > now) return false; // backing off
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
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            var runCompleted = false;
            try
            {
                if (ct.IsCancellationRequested) break;

                await ThrottleAsync(ct);
                var result = await gradingService.GradeSongAsync(item.SongId, item.Force, ct);

                switch (result.Outcome)
                {
                    case GradeOutcome.Graded:
                        Interlocked.Exchange(ref _warnedNotConfigured, 0);
                        _failedUntil.TryRemove(item.SongId, out _); // recovered — clear any backoff
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
                runCompleted = channel.MarkProcessed();
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
        _failedUntil[songId] = DateTime.UtcNow + TimeSpan.FromSeconds(options.CurrentValue.FailureBackoffSeconds);

    /// <summary>Spaces out calls to honour <see cref="QualityGradingOptions.RequestsPerSecond"/> across all workers.</summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        var rps = Math.Max(1, options.CurrentValue.RequestsPerSecond);
        var minInterval = TimeSpan.FromSeconds(1.0 / rps);

        await _rateLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var wait = _nextSlotUtc - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            var baseTime = now > _nextSlotUtc ? now : _nextSlotUtc;
            _nextSlotUtc = baseTime + minInterval;
        }
        finally
        {
            _rateLock.Release();
        }
    }
}
