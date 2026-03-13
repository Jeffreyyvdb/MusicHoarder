using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace MusicHoarder.Api.Scanner;

public record ScanRequest(Guid ScanId);

public class ScannerBackgroundService(
    IServiceScopeFactory scopeFactory,
    Channel<ScanRequest> channel,
    ILogger<ScannerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation("Starting scan, scanId: {ScanId}", request.ScanId);

                using var scope = scopeFactory.CreateScope();
                var indexService = scope.ServiceProvider.GetRequiredService<IIndexService>();

                var progress = new Progress<IndexProgress>(p =>
                {
                    if (p.Scanned % 100 == 0 || p.Scanned == p.TotalFiles)
                    {
                        logger.LogInformation("Progress: {Scanned}/{Total} files (New: {New}, Changed: {Changed}, Deleted: {Deleted})", 
                            p.Scanned, p.TotalFiles, p.NewFiles, p.ChangedFiles, p.DeletedFiles);
                    }
                });

                var result = await indexService.IndexAsync("/Volumes/music", progress, stoppingToken);

                logger.LogInformation("Scan complete, scanId: {ScanId}, Total: {Total}, New: {New}, Changed: {Changed}, Deleted: {Deleted}, Duration: {Duration}s",
                    request.ScanId, result.TotalFiles, result.NewFiles, result.ChangedFiles, result.DeletedFiles, result.Duration.TotalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan failed {ScanId}", request.ScanId);
            }
        }
    }
}
