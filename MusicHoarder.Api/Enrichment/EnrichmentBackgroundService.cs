using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Enrichment;

public class EnrichmentBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    EnrichmentProgressTracker progressTracker,
    IEnrichmentOrchestrator orchestrator,
    IOptions<MusicEnricherOptions> options,
    ILogger<EnrichmentBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation(
            "Enrichment background service started. BatchSize={BatchSize}, Concurrency={Concurrency}",
            opts.EnrichmentBatchSize,
            opts.EnrichmentWorkerConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            CancellationToken jobToken;
            int pendingCount;

            // 1. Check for a manual trigger from the HTTP endpoint first.
            if (jobManager.EnrichTriggers.TryRead(out var manualJobId))
            {
                jobId = manualJobId;
                jobToken = jobManager.GetCurrentCancellationToken();
                pendingCount = await CountPendingAsync(stoppingToken);
            }
            else
            {
                // 2. Auto-poll: check if there is pending work.
                pendingCount = await CountPendingAsync(stoppingToken);

                if (pendingCount == 0)
                {
                    // Idle: wait for a manual trigger or the idle delay, whichever comes first.
                    var triggerTask = jobManager.EnrichTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(opts.EnrichmentIdleDelaySeconds), stoppingToken);
                    await Task.WhenAny(triggerTask, delayTask);
                    continue;
                }

                // Try to acquire the global job lock before starting an auto-triggered cycle.
                jobId = Guid.NewGuid();
                if (!jobManager.TryRegisterAutoJob(JobType.Enrich, jobId, out jobToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.EnrichmentIdleDelaySeconds), stoppingToken);
                    continue;
                }
            }

            var cycleStarted = false;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
            var ct = linkedCts.Token;

            try
            {
                progressTracker.StartCycle(jobId, pendingCount);
                cycleStarted = true;

                logger.LogInformation(
                    "Starting enrichment cycle {RunId} with {PendingCount} pending tracks",
                    jobId, pendingCount);

                while (!ct.IsCancellationRequested)
                {
                    var result = await orchestrator.ProcessNextBatchAsync(jobId, ct);
                    if (result.TotalTracks == 0) break;

                    var state = progressTracker.GetCurrent();
                    if (state is { RunId: var stateRunId, Processed: var processed }
                        && stateRunId == jobId
                        && processed >= pendingCount)
                    {
                        break;
                    }
                }

                progressTracker.CompleteCycle(jobId);
                var wasCancelled = ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested;
                jobManager.SignalComplete(jobId, wasCancelled);

                if (wasCancelled)
                    logger.LogInformation("Enrichment run {RunId} cancelled via cancel endpoint", jobId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (cycleStarted) progressTracker.CompleteCycle(jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
                logger.LogInformation("Enrichment run {RunId} stopped with application", jobId);
            }
            catch (Exception ex)
            {
                if (cycleStarted) progressTracker.CompleteCycle(jobId);
                jobManager.SignalFailed(jobId);
                logger.LogError(ex, "Enrichment run {RunId} failed", jobId);
                await Task.Delay(TimeSpan.FromSeconds(opts.EnrichmentIdleDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task<int> CountPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        return await db.Songs
            .AsNoTracking()
            .WhereReadyForEnrichment()
            .CountAsync(ct);
    }
}
