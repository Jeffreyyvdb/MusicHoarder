using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Library;

public class LibraryBuilderBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MusicEnricherOptions> options,
    ILogger<LibraryBuilderBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation(
            "Library builder background service started. BatchSize={BatchSize}, Concurrency={Concurrency}",
            opts.LibraryBuilderBatchSize,
            opts.LibraryBuilderWorkerConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            var runId = Guid.NewGuid();

            try
            {
                using var scope = scopeFactory.CreateScope();
                var builder = scope.ServiceProvider.GetRequiredService<ILibraryBuilderService>();

                var result = await builder.ProcessNextBatchAsync(runId, stoppingToken);
                if (result.TotalTracks == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.LibraryBuilderIdleDelaySeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Library builder run {RunId} cancelled", runId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Library builder run {RunId} failed", runId);
                await Task.Delay(TimeSpan.FromSeconds(opts.LibraryBuilderIdleDelaySeconds), stoppingToken);
            }
        }
    }
}
