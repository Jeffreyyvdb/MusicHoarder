using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

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
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<QualityGradingBackgroundService> logger) : BackgroundService
{
    private static readonly EnrichmentStatus[] GradeableStatuses =
        [EnrichmentStatus.Matched, EnrichmentStatus.NeedsReview];

    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTime _nextSlotUtc = DateTime.MinValue;
    private int _warnedNotConfigured;

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
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
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

        var needsGrading = candidates.Where(c =>
        {
            if (!latest.TryGetValue(c.Id, out var g)) return true;            // never graded
            if (g.PromptVersion != QualityGradingPrompt.Version) return true; // prompt changed
            if (g.Model != opts.Model) return true;                          // model changed
            if (c.EnrichedAtUtc is { } e && g.GradedAtUtc < e) return true;  // re-enriched since
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
            try
            {
                if (ct.IsCancellationRequested) break;

                await ThrottleAsync(ct);
                var result = await gradingService.GradeSongAsync(item.SongId, item.Force, ct);

                switch (result.Outcome)
                {
                    case GradeOutcome.Graded:
                        Interlocked.Exchange(ref _warnedNotConfigured, 0);
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
                logger.LogWarning(ex, "Quality grading worker {WorkerId} failed on song {SongId}", workerId, item.SongId);
                progressTracker.RecordError("error", ex.Message);
                progressTracker.IncrementFailed();
            }
            finally
            {
                channel.MarkProcessed();
            }
        }
    }

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
