using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

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

            try
            {
                using var scope = scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IEnrichmentOrchestrator>();

                var result = await orchestrator.ProcessNextBatchAsync(runId, stoppingToken);
                if (result.TotalTracks == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.EnrichmentIdleDelaySeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Enrichment run {RunId} cancelled", runId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Enrichment run {RunId} failed", runId);
                await Task.Delay(TimeSpan.FromSeconds(opts.EnrichmentIdleDelaySeconds), stoppingToken);
            }
        }
    }
}
