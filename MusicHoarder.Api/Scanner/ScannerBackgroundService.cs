using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Scanner;

public record ScanRequest(Guid ScanId);

public class ScannerBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    IOptions<MusicEnricherOptions> options,
    ILogger<ScannerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in jobManager.ScanTriggers.ReadAllAsync(stoppingToken))
        {
            var jobToken = jobManager.GetCurrentCancellationToken();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
            var ct = linkedCts.Token;

            try
            {
                logger.LogInformation("Starting scan job {JobId}", jobId);

                using var scope = scopeFactory.CreateScope();
                var indexService = scope.ServiceProvider.GetRequiredService<IIndexService>();

                var result = await indexService.IndexAsync(
                    jobId,
                    options.Value.SourceDirectory,
                    ct);

                logger.LogInformation(
                    "Scan {JobId} complete — Total: {Total}, New: {New}, Changed: {Changed}, Deleted: {Deleted}, Skipped: {Skipped}, Failed: {Failed}, Duration: {Duration:F1}s",
                    jobId, result.TotalFiles, result.NewFiles, result.ChangedFiles,
                    result.DeletedFiles, result.SkippedFiles, result.FailedFiles, result.Duration.TotalSeconds);

                jobManager.SignalComplete(jobId);

                if (result.NewFiles + result.ChangedFiles > 0
                    && jobManager.TryStartJob(JobType.Fingerprint, out var fpJobId, out _))
                {
                    logger.LogInformation(
                        "Auto-triggered fingerprint job {FpJobId} after scan {ScanJobId}",
                        fpJobId, jobId);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Scan {JobId} cancelled via cancel endpoint", jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Scan {JobId} stopped with application", jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan {JobId} failed", jobId);
                jobManager.SignalFailed(jobId);
            }
        }
    }
}
