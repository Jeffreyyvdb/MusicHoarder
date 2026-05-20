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

                // 2. Auto-poll: check if there is pending work.
                var pendingCount = await CountPendingAsync(stoppingToken);

                if (pendingCount == 0)
                {
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

    private async Task<int> CountPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        return await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .Where(s => !s.IsDuplicate)
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched)
            .Where(s => s.LibraryBuildStatus != LibraryBuildStatus.Done
                || s.DestinationPath == null
                || s.PreviousDestinationPath != null)
            .CountAsync(ct);
    }
}
