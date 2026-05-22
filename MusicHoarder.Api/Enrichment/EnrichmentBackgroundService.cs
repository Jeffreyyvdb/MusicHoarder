using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public class EnrichmentBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    EnrichmentProgressTracker progressTracker,
    EnrichmentPipelineChannel pipelineChannel,
    IEnrichmentOrchestrator orchestrator,
    IOptions<MusicEnricherOptions> options,
    ILogger<EnrichmentBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation(
            "Enrichment background service started (channel-fed). WorkerConcurrency={Concurrency}",
            opts.EnrichmentWorkerConcurrency);

        // Workers always run so explicitly-enqueued songs (manual fingerprint, per-song/per-folder
        // enrich) get processed. The startup sweeps and periodic retry sweep are auto-discovery, so
        // they're gated behind AutoStartPipeline.
        Task sweepTask = Task.CompletedTask;
        if (opts.AutoStartPipeline)
        {
            await RefreshStaleStatusesAsync(stoppingToken);
            await RetryStaleStatusesAsync(stoppingToken);
            await BackfillPendingSongsAsync(stoppingToken);
            sweepTask = RunRetrySweepLoopAsync(stoppingToken);
        }

        var workerTasks = Enumerable.Range(0, opts.EnrichmentWorkerConcurrency)
            .Select(i => RunWorkerAsync(i, stoppingToken))
            .ToArray();

        await Task.WhenAll([sweepTask, .. workerTasks]);
    }

    /// <summary>
    /// Recomputes summary status for every non-Pending song against the currently
    /// enabled provider set. Songs whose status would now be Pending (because a
    /// newly-enabled provider has no attempt yet) are flipped and enqueued.
    /// ProviderAttempts are kept — the orchestrator skips providers with terminal
    /// attempts, so the re-run only hits the new provider.
    /// </summary>
    internal async Task RefreshStaleStatusesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var enabled = await orchestrator.GetEnabledProviderEnumsAsync(ct).ConfigureAwait(false);

            if (enabled.Count == 0)
                return;

            var candidates = await db.Songs
                .IgnoreQueryFilters()
                .Include(s => s.ProviderAttempts)
                .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
                .Where(s => s.EnrichmentStatus != EnrichmentStatus.Pending)
                .ToListAsync(ct);

            var flipped = new List<int>();
            foreach (var song in candidates)
            {
                if (song.ComputeSummaryStatus(enabled) != EnrichmentStatus.Pending)
                    continue;

                song.EnrichmentStatus = EnrichmentStatus.Pending;
                song.EnrichmentError = null;
                flipped.Add(song.Id);
            }

            if (flipped.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                pipelineChannel.EnqueueRange(flipped);
                logger.LogInformation(
                    "Refresh sweep flipped {Count} songs to Pending after provider-set change",
                    flipped.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to refresh stale enrichment statuses on startup");
        }
    }

    /// <summary>
    /// Opt-in: when <see cref="MusicEnricherOptions.RetryNeedsReviewOnStartup"/> or
    /// <see cref="MusicEnricherOptions.RetryFailedOnStartup"/> is set, reset matching
    /// rows back to Pending (clearing ProviderAttempts so every enabled provider
    /// retries from scratch) and enqueue them.
    /// </summary>
    internal async Task RetryStaleStatusesAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.RetryNeedsReviewOnStartup && !opts.RetryFailedOnStartup)
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

            var statusesToRetry = new List<EnrichmentStatus>();
            if (opts.RetryNeedsReviewOnStartup) statusesToRetry.Add(EnrichmentStatus.NeedsReview);
            if (opts.RetryFailedOnStartup) statusesToRetry.Add(EnrichmentStatus.Failed);

            var candidates = await db.Songs
                .IgnoreQueryFilters()
                .Include(s => s.ProviderAttempts)
                .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
                .Where(s => statusesToRetry.Contains(s.EnrichmentStatus))
                .ToListAsync(ct);

            if (candidates.Count == 0)
                return;

            foreach (var song in candidates)
                song.ResetEnrichment(restoreOriginal: false);

            await db.SaveChangesAsync(ct);
            pipelineChannel.EnqueueRange(candidates.Select(s => s.Id));
            logger.LogInformation(
                "Retry sweep reset {Count} songs to Pending (RetryNeedsReview={NR}, RetryFailed={F})",
                candidates.Count, opts.RetryNeedsReviewOnStartup, opts.RetryFailedOnStartup);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to retry stale enrichment statuses on startup");
        }
    }

    private async Task BackfillPendingSongsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

            var pendingIds = await db.Songs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => !s.IsSynthetic)
                .WhereReadyForEnrichment()
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(ct);

            if (pendingIds.Count > 0)
            {
                pipelineChannel.EnqueueRange(pendingIds);
                logger.LogInformation("Backfilled {Count} pending songs into enrichment channel", pendingIds.Count);
            }

            var retryIds = await db.SongProviderAttempts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .WhereRetryableProviderAttempts(DateTime.UtcNow)
                .ToListAsync(ct);

            if (retryIds.Count > 0)
            {
                pipelineChannel.EnqueueRange(retryIds);
                logger.LogInformation("Backfilled {Count} retryable songs into enrichment channel", retryIds.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to backfill pending/retryable songs on startup");
        }
    }

    private async Task RunRetrySweepLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(options.Value.EnrichmentRetrySweepIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

                var retryIds = await db.SongProviderAttempts
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .WhereRetryableProviderAttempts(DateTime.UtcNow)
                    .ToListAsync(ct);

                if (retryIds.Count > 0)
                {
                    pipelineChannel.EnqueueRange(retryIds);
                    logger.LogInformation("Retry sweep enqueued {Count} rate-limited songs", retryIds.Count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retry sweep failed");
            }
        }
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        logger.LogDebug("Enrichment worker {WorkerId} started", workerId);

        await foreach (var songId in pipelineChannel.Reader.ReadAllAsync(ct))
        {
            if (ct.IsCancellationRequested)
                break;

            if (jobManager.IsStepPaused(JobType.Enrich))
            {
                await WaitUntilResumedAsync(ct);
                if (ct.IsCancellationRequested)
                    break;
            }

            try
            {
                var outcome = await orchestrator.ProcessSongAsync(songId, ct);

                switch (outcome)
                {
                    case EnrichmentOutcome.Matched:
                        progressTracker.IncrementEnriched();
                        break;
                    case EnrichmentOutcome.NeedsReview:
                        progressTracker.IncrementNeedsReview();
                        break;
                    case EnrichmentOutcome.Failed:
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
                logger.LogWarning(ex, "Enrichment worker {WorkerId} failed processing song {SongId}",
                    workerId, songId);
                progressTracker.IncrementFailed();
            }
        }

        logger.LogDebug("Enrichment worker {WorkerId} stopped", workerId);
    }

    private async Task WaitUntilResumedAsync(CancellationToken ct)
    {
        while (jobManager.IsStepPaused(JobType.Enrich) && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
