using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Enrichment;

public class EnrichmentBackgroundService(
    IServiceScopeFactory scopeFactory,
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
            var runId = Guid.NewGuid();
            var cycleStarted = false;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IEnrichmentOrchestrator>();
                var progressTracker = scope.ServiceProvider.GetRequiredService<EnrichmentProgressTracker>();

                var pendingCount = await dbContext.Songs
                    .AsNoTracking()
                    .Where(s => s.DeletedAtUtc == null)
                    .Where(s => s.Fingerprint != null && s.Fingerprint != string.Empty)
                    .Where(s => s.DurationSeconds != null)
                    .Where(s => s.EnrichmentStatus == EnrichmentStatus.Pending)
                    .CountAsync(stoppingToken);

                if (pendingCount == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.EnrichmentIdleDelaySeconds), stoppingToken);
                    continue;
                }

                progressTracker.StartCycle(runId, pendingCount);
                cycleStarted = true;
                logger.LogInformation(
                    "Starting enrichment cycle {RunId} with {PendingCount} pending tracks",
                    runId,
                    pendingCount);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var result = await orchestrator.ProcessNextBatchAsync(runId, stoppingToken);
                    if (result.TotalTracks == 0)
                    {
                        break;
                    }

                    var state = progressTracker.GetCurrent();
                    if (state is { RunId: var stateRunId, Processed: var processed }
                        && stateRunId == runId
                        && processed >= pendingCount)
                    {
                        break;
                    }
                }

                progressTracker.CompleteCycle(runId);
                cycleStarted = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Enrichment run {RunId} cancelled", runId);
            }
            catch (Exception ex)
            {
                if (cycleStarted)
                {
                    using var scope = scopeFactory.CreateScope();
                    var progressTracker = scope.ServiceProvider.GetRequiredService<EnrichmentProgressTracker>();
                    progressTracker.CompleteCycle(runId);
                }
                logger.LogError(ex, "Enrichment run {RunId} failed", runId);
                await Task.Delay(TimeSpan.FromSeconds(opts.EnrichmentIdleDelaySeconds), stoppingToken);
            }
        }
    }
}
