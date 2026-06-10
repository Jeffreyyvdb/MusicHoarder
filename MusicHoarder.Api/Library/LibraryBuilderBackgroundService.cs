using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Library;

public class LibraryBuilderBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    LibraryBuilderProgressTracker progressTracker,
    IDirectoryAvailability directoryAvailability,
    IOptions<MusicEnricherOptions> options,
    ILogger<LibraryBuilderBackgroundService> logger) : BackgroundService
{
    // Last time the split-album self-heal ran (either at a run start or from the idle sweep).
    // MinValue so the first idle poll after boot heals immediately — that's what picks up albums
    // that were split by builds pre-dating the safeguard.
    private DateTime _lastHealUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation(
            "Library builder background service started. BatchSize={BatchSize}, Concurrency={Concurrency}",
            opts.LibraryBuilderBatchSize,
            opts.LibraryBuilderWorkerConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            CancellationToken jobToken;

            // The builder copies from source and writes to destination, so it needs both
            // reachable. When offline (e.g. travelling away from the home NAS) idle-wait
            // instead of failing every track.
            var directoriesAvailable = directoryAvailability.Current.AllAvailable;

            // 1. Check for a manual trigger from the HTTP endpoint first.
            if (jobManager.BuildTriggers.TryRead(out var manualJobId))
            {
                jobId = manualJobId;
                jobToken = jobManager.GetCurrentCancellationToken();
                if (!directoriesAvailable)
                {
                    logger.LogWarning("Library build {JobId} skipped — source/destination directory is offline", jobId);
                    jobManager.SignalComplete(jobId, cancelled: true);
                    continue;
                }
            }
            else
            {
                if (!directoriesAvailable)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(opts.LibraryBuilderIdleDelaySeconds), stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Manual mode: don't auto-discover pending work — wait for an explicit trigger.
                if (!opts.AutoStartPipeline)
                {
                    var manualTrigger = jobManager.BuildTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var manualDelay = Task.Delay(TimeSpan.FromSeconds(opts.LibraryBuilderIdleDelaySeconds), stoppingToken);
                    try { await Task.WhenAny(manualTrigger, manualDelay); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // 2. Auto-poll: check if there is pending work.
                var pendingCount = await CountPendingAsync(stoppingToken);

                if (pendingCount == 0)
                {
                    // Fully built, nothing pending — the run-start heal below would never be
                    // reached, so legacy split albums (all rows Done) would persist forever.
                    // Sweep for them here, at most once per interval; any re-queued rows make the
                    // next pending poll non-zero and the normal auto-job flow builds them.
                    if (await TryIdleHealAsync(opts, stoppingToken))
                        continue;

                    // Idle: wait for a manual trigger or the idle delay, whichever comes first.
                    var triggerTask = jobManager.BuildTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(opts.LibraryBuilderIdleDelaySeconds), stoppingToken);
                    await Task.WhenAny(triggerTask, delayTask);
                    continue;
                }

                // Try to acquire the global job lock before starting an auto-triggered cycle.
                jobId = Guid.NewGuid();
                if (!jobManager.TryRegisterAutoJob(JobType.Build, jobId, out jobToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.LibraryBuilderIdleDelaySeconds), stoppingToken);
                    continue;
                }
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
            var ct = linkedCts.Token;
            var runStarted = false;

            try
            {
                progressTracker.StartRun(jobId);
                runStarted = true;

                logger.LogInformation("Starting library build run {RunId}", jobId);

                // Split-album safeguard: converge every logical album on one persisted identity
                // BEFORE any batch elects per-folder, so cross-folder splits are pulled into one
                // folder and stale Done siblings are re-queued into this very run.
                await HealSplitAlbumsAsync(opts, ct);

                while (!ct.IsCancellationRequested)
                {
                    using var scope = scopeFactory.CreateScope();
                    var builder = scope.ServiceProvider.GetRequiredService<ILibraryBuilderService>();

                    var result = await builder.ProcessNextBatchAsync(jobId, ct);
                    if (result.TotalTracks == 0) break;

                    progressTracker.AddTotal(result.TotalTracks);
                    for (var i = 0; i < result.Done; i++) progressTracker.IncrementBuilt();
                    for (var i = 0; i < result.Failed; i++) progressTracker.IncrementFailed();
                }

                progressTracker.CompleteRun(jobId);
                var wasCancelled = ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested;
                jobManager.SignalComplete(jobId, wasCancelled);

                if (wasCancelled)
                    logger.LogInformation("Library build run {RunId} cancelled via cancel endpoint", jobId);
                else
                    logger.LogInformation("Library build run {RunId} complete", jobId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
                logger.LogInformation("Library build run {RunId} stopped with application", jobId);
            }
            catch (Exception ex)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalFailed(jobId);
                logger.LogError(ex, "Library builder run {RunId} failed", jobId);
                await Task.Delay(TimeSpan.FromSeconds(opts.LibraryBuilderIdleDelaySeconds), stoppingToken);
            }
        }
    }

    // Idle-time sweep wrapper: rate-limited by AlbumSplitHealIntervalMinutes. Returns true when it
    // re-queued rows, so the caller can re-poll for pending work immediately instead of idling.
    private async Task<bool> TryIdleHealAsync(MusicEnricherOptions opts, CancellationToken ct)
    {
        if (!opts.EnableAlbumIdentityReconciliation || !opts.EnableAlbumSplitSelfHeal)
            return false;
        if (DateTime.UtcNow - _lastHealUtc < TimeSpan.FromMinutes(opts.AlbumSplitHealIntervalMinutes))
            return false;

        var result = await HealSplitAlbumsAsync(opts, ct);
        return result is { SongsRequeued: > 0 };
    }

    // A heal failure must never take down a build run (or the idle loop) — the per-folder
    // reconciliation still tags whatever does build consistently; log and move on.
    private async Task<AlbumSplitHealResult?> HealSplitAlbumsAsync(MusicEnricherOptions opts, CancellationToken ct)
    {
        if (!opts.EnableAlbumIdentityReconciliation || !opts.EnableAlbumSplitSelfHeal)
            return null;

        _lastHealUtc = DateTime.UtcNow;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var healer = scope.ServiceProvider.GetRequiredService<IAlbumSplitHealer>();
            var result = await healer.HealAsync(ct);
            if (result.GroupsHealed > 0)
            {
                logger.LogInformation(
                    "Split-album self-heal: {Groups} albums converged, {Corrected} songs corrected, {Requeued} re-queued for re-tag",
                    result.GroupsHealed, result.SongsCorrected, result.SongsRequeued);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Split-album self-heal failed; continuing without it");
            return null;
        }
    }

    private async Task<int> CountPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        // Same candidate set the batch query uses — including the lyrics-wait gate — so the poll never
        // reports work the builder would skip (which would busy-loop the idle waiter).
        return await LibraryBuildQuery.BuildCandidates(
                db.Songs.IgnoreQueryFilters().AsNoTracking(),
                LibraryBuildQuery.LyricsWaitCutoff(options.Value))
            .CountAsync(ct);
    }
}
