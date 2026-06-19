using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Pipeline stage that fetches Pending <see cref="WishlistItem"/>s via the configured
/// <see cref="IDownloadProvider"/> into the writable <see cref="MusicEnricherOptions.DownloadDirectory"/>
/// staging dir, then auto-triggers a scan so the existing scan→fingerprint→enrich→build pipeline ingests
/// the files. Items already in the local library (an exact <c>InLibrary</c> match in the Spotify match
/// cache) are skipped, not downloaded. Runs trigger-first then auto-sweeps, mirroring
/// <see cref="Scanner.FingerprintBackgroundService"/>. The batch logic lives in
/// <see cref="WishlistDownloadProcessor"/> (testable in isolation).
/// </summary>
public class DownloadBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    DownloadProgressTracker progressTracker,
    IOwnerLookupService ownerLookup,
    IOptions<MusicEnricherOptions> options,
    ILogger<DownloadBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation(
            "Download background service started. Enabled={Enabled}, Provider={Provider}, Concurrency={Concurrency}",
            opts.EnableWishlistDownloads, opts.DownloadProvider, opts.DownloadConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Link any already-downloaded items the scanner has since ingested, regardless of new work.
            try
            {
                await RunInScopeAsync((db, processor, ownerId) =>
                    processor.LinkDownloadedItemsAsync(db, ownerId, stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Wishlist download linker pass failed");
            }

            opts = options.Value;
            Guid jobId;
            CancellationToken jobToken;
            int pendingCount;

            // Downloads land in the dedicated writable staging dir (not the read-only source mount).
            // Ready = feature on + a directory configured that we can create.
            var ready = opts.EnableWishlistDownloads && EnsureDownloadDirectory(opts.DownloadDirectory);

            if (jobManager.DownloadTriggers.TryRead(out var manualJobId))
            {
                jobId = manualJobId;
                jobToken = jobManager.GetCurrentCancellationToken();
                if (!ready)
                {
                    logger.LogInformation(
                        "Download {JobId} skipped — {Reason}", jobId,
                        !opts.EnableWishlistDownloads
                            ? "wishlist downloads are disabled"
                            : "no writable download directory is configured");
                    jobManager.SignalComplete(jobId, cancelled: true);
                    continue;
                }
                pendingCount = await CountPendingAsync(stoppingToken);
            }
            else
            {
                // No explicit trigger queued. Only auto-sweep Pending items in the background when the
                // feature is ready AND auto-download is enabled; otherwise wait for an explicit trigger
                // so the instance never fetches on its own (e.g. PR previews stay manual/opt-in, while
                // production auto-downloads for the owner).
                var autoSweep = ready && opts.AutoDownloadWishlist;
                pendingCount = autoSweep ? await CountPendingAsync(stoppingToken) : 0;

                if (!autoSweep || pendingCount == 0)
                {
                    var triggerTask = jobManager.DownloadTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(opts.DownloadIdleDelaySeconds), stoppingToken);
                    await Task.WhenAny(triggerTask, delayTask);
                    continue;
                }

                jobId = Guid.NewGuid();
                if (!jobManager.TryRegisterAutoJob(JobType.Download, jobId, out jobToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.DownloadIdleDelaySeconds), stoppingToken);
                    continue;
                }
            }

            var runStarted = false;
            var producedFiles = false;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
            var ct = linkedCts.Token;

            try
            {
                progressTracker.StartRun(jobId, pendingCount);
                runStarted = true;
                logger.LogInformation("Starting download run {RunId} with {PendingCount} pending items", jobId, pendingCount);

                while (!ct.IsCancellationRequested)
                {
                    var (processed, downloaded) = await RunInScopeAsync((db, processor, ownerId) =>
                        processor.ProcessBatchAsync(db, ownerId, ct), ct);
                    if (downloaded > 0) producedFiles = true;
                    if (processed == 0) break;
                }

                progressTracker.CompleteRun(jobId);
                var wasCancelled = ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested;
                jobManager.SignalComplete(jobId, wasCancelled);

                // New files landed in the source tree — wake the scanner so the pipeline ingests them.
                if (!wasCancelled && producedFiles
                    && jobManager.TryStartJob(JobType.Scan, out var scanJobId, out _))
                {
                    logger.LogInformation("Auto-triggered scan {ScanJobId} after download run {RunId}", scanJobId, jobId);
                }

                logger.LogInformation("Download run {RunId} {Outcome}", jobId, wasCancelled ? "cancelled" : "complete");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
                logger.LogInformation("Download run {RunId} stopped with application", jobId);
            }
            catch (Exception ex)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalFailed(jobId);
                logger.LogError(ex, "Download run {RunId} failed", jobId);
                await Task.Delay(TimeSpan.FromSeconds(opts.DownloadIdleDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task<T> RunInScopeAsync<T>(
        Func<MusicHoarderDbContext, WishlistDownloadProcessor, Guid, Task<T>> work, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<WishlistDownloadProcessor>();
        return await work(db, processor, ownerLookup.OwnerUserId);
    }

    private async Task<int> CountPendingAsync(CancellationToken ct)
    {
        var ownerId = ownerLookup.OwnerUserId;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        return await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.OwnerUserId != WellKnownUsers.DemoId)
            .Where(w => w.Status == WishlistItemStatus.Pending)
            .CountAsync(ct);
    }

    /// <summary>Returns true if a download directory is configured and exists (creating it if needed).</summary>
    private bool EnsureDownloadDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wishlist download directory {Directory} is not writable", directory);
            return false;
        }
    }
}
