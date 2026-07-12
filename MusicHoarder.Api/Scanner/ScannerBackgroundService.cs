using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Pipeline;

namespace MusicHoarder.Api.Scanner;

public record ScanRequest(Guid ScanId);

public class ScannerBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    IDirectoryAvailability directoryAvailability,
    IOptions<MusicEnricherOptions> options,
    IOptions<SyncOptions> syncOptions,
    ILogger<ScannerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The initial/reconnect scan is owned by DirectoryAvailabilityMonitor, which only
        // triggers once the source directory is actually reachable.
        await foreach (var jobId in jobManager.ScanTriggers.ReadAllAsync(stoppingToken))
        {
            var jobToken = jobManager.GetCurrentCancellationToken();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
            var ct = linkedCts.Token;

            // Scan roots: the (read-only) source library when reachable, plus the writable wishlist
            // download staging dir when configured & present. Each root reconciles deletions only
            // within its own path prefix, so an offline source root never wipes downloaded tracks.
            var roots = new List<string>();
            if (directoryAvailability.Current.SourceAvailable)
                roots.Add(options.Value.SourceDirectory);
            var downloadDir = options.Value.DownloadDirectory;
            if (!string.IsNullOrWhiteSpace(downloadDir) && Directory.Exists(downloadDir))
                roots.Add(downloadDir);
            // Sync-receive managed dir: rows are created directly by the ingest, so scanning it is
            // the crash-recovery safety net (a file orphaned between move and row-insert gets a
            // normal re-enriching ingest). In-flight uploads live in .incoming/, which the indexer
            // skips as a hidden directory.
            var syncedDir = syncOptions.Value.SyncedSourceDirectory;
            if (syncOptions.Value.Mode == SyncMode.Receive
                && !string.IsNullOrWhiteSpace(syncedDir) && Directory.Exists(syncedDir))
                roots.Add(syncedDir);

            if (roots.Count == 0)
            {
                logger.LogInformation(
                    "Scan {JobId} skipped — no scan roots available (source offline, no download dir)", jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
                continue;
            }

            try
            {
                logger.LogInformation("Starting scan job {JobId} over {RootCount} root(s)", jobId, roots.Count);

                using var scope = scopeFactory.CreateScope();
                var indexService = scope.ServiceProvider.GetRequiredService<IIndexService>();

                var newOrChanged = 0;
                foreach (var root in roots)
                {
                    var result = await indexService.IndexAsync(jobId, root, ct);
                    newOrChanged += result.NewFiles + result.ChangedFiles;

                    logger.LogInformation(
                        "Scan {JobId} root {Root} — Total: {Total}, New: {New}, Changed: {Changed}, Deleted: {Deleted}, Skipped: {Skipped}, Failed: {Failed}, Duration: {Duration:F1}s",
                        jobId, root, result.TotalFiles, result.NewFiles, result.ChangedFiles,
                        result.DeletedFiles, result.SkippedFiles, result.FailedFiles, result.Duration.TotalSeconds);
                }

                jobManager.SignalComplete(jobId);

                // Always consume the one-shot flag (even in auto mode) so it never leaks. A
                // download/import-initiated scan cascades to fingerprint regardless of AutoStartPipeline
                // so the explicitly-acquired track flows to the library instead of stalling in manual mode.
                var forceCascade = jobManager.ConsumeForcePipelineCascade(jobId);
                if ((options.Value.AutoStartPipeline || forceCascade)
                    && newOrChanged > 0
                    && jobManager.TryStartJob(JobType.Fingerprint, out var fpJobId, out _))
                {
                    logger.LogInformation(
                        "Auto-triggered fingerprint job {FpJobId} after scan {ScanJobId}{Forced}",
                        fpJobId, jobId, forceCascade && !options.Value.AutoStartPipeline ? " (acquisition-forced)" : "");
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
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                // Source went unreachable mid-scan (network drop). Treat as offline, not a hard
                // failure, and re-probe so the availability snapshot / UI banner update promptly.
                logger.LogWarning(
                    "Scan {JobId} aborted — source directory {SourceDirectory} became unreachable: {Message}",
                    jobId, options.Value.SourceDirectory, ex.Message);
                jobManager.SignalComplete(jobId, cancelled: true);
                _ = directoryAvailability.ProbeNowAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan {JobId} failed", jobId);
                jobManager.SignalFailed(jobId);
            }
        }
    }
}
