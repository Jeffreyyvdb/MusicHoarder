using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Scanner;

public record ScanRequest(Guid ScanId);

public class ScannerBackgroundService(
    IServiceScopeFactory scopeFactory,
    Channel<ScanRequest> channel,
    IOptions<MusicEnricherOptions> options,
    ILogger<ScannerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation("Starting scan {ScanId}", request.ScanId);

                using var scope = scopeFactory.CreateScope();
                var indexService = scope.ServiceProvider.GetRequiredService<IIndexService>();

                var result = await indexService.IndexAsync(
                    request.ScanId,
                    options.Value.SourceDirectory,
                    stoppingToken);

                logger.LogInformation(
                    "Scan {ScanId} complete — Total: {Total}, New: {New}, Changed: {Changed}, Deleted: {Deleted}, Skipped: {Skipped}, Failed: {Failed}, Duration: {Duration:F1}s",
                    request.ScanId, result.TotalFiles, result.NewFiles, result.ChangedFiles,
                    result.DeletedFiles, result.SkippedFiles, result.FailedFiles, result.Duration.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Scan {ScanId} cancelled", request.ScanId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan {ScanId} failed", request.ScanId);
            }
        }
    }
}
