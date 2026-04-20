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

        await BackfillPendingSongsAsync(stoppingToken);

        var sweepTask = RunRetrySweepLoopAsync(stoppingToken);

        var workerTasks = Enumerable.Range(0, opts.EnrichmentWorkerConcurrency)
            .Select(i => RunWorkerAsync(i, stoppingToken))
            .ToArray();

        await Task.WhenAll([sweepTask, .. workerTasks]);
    }

    private async Task BackfillPendingSongsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

            var pendingIds = await db.Songs
                .AsNoTracking()
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
