using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;

namespace MusicHoarder.Api.Scanner;

public class FingerprintBackgroundService(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    FingerprintProgressTracker progressTracker,
    IFpcalcService fpcalcService,
    IDuplicateDetectionService duplicateDetectionService,
    EnrichmentPipelineChannel enrichmentChannel,
    IDirectoryAvailability directoryAvailability,
    IOptions<MusicEnricherOptions> options,
    ILogger<FingerprintBackgroundService> logger) : BackgroundService
{
    // Songs where fpcalc permanently failed in this service lifetime.
    // Cleared on service restart so failed files get retried after a redeploy.
    private readonly HashSet<int> _permanentlyFailed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation(
            "Fingerprint background service started. Concurrency={Concurrency}, BatchSize={BatchSize}",
            opts.FingerprintConcurrency,
            opts.FingerprintBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            CancellationToken jobToken;
            int pendingCount;

            // Fingerprinting reads the source files via fpcalc; if the source is offline we'd
            // just mark every track permanently-failed. Idle-wait until it's reachable again.
            var sourceAvailable = directoryAvailability.Current.SourceAvailable;

            if (jobManager.FingerprintTriggers.TryRead(out var manualJobId))
            {
                jobId = manualJobId;
                jobToken = jobManager.GetCurrentCancellationToken();
                if (!sourceAvailable)
                {
                    logger.LogWarning("Fingerprint {JobId} skipped — source directory is offline", jobId);
                    jobManager.SignalComplete(jobId, cancelled: true);
                    continue;
                }
                pendingCount = await CountPendingAsync(stoppingToken);
            }
            else
            {
                if (!sourceAvailable)
                {
                    if (!await DelayIdleAsync(opts.FingerprintIdleDelaySeconds, stoppingToken)) break;
                    continue;
                }

                // Manual mode: don't auto-discover pending work — wait for an explicit trigger.
                if (!opts.AutoStartPipeline)
                {
                    var manualTrigger = jobManager.FingerprintTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var manualDelay = Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
                    await Task.WhenAny(manualTrigger, manualDelay);
                    continue;
                }

                pendingCount = await CountPendingAsync(stoppingToken);

                if (pendingCount == 0)
                {
                    var triggerTask = jobManager.FingerprintTriggers.WaitToReadAsync(stoppingToken).AsTask();
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
                    await Task.WhenAny(triggerTask, delayTask);
                    continue;
                }

                jobId = Guid.NewGuid();
                if (!jobManager.TryRegisterAutoJob(JobType.Fingerprint, jobId, out jobToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
                    continue;
                }
            }

            var runStarted = false;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
            var ct = linkedCts.Token;

            try
            {
                progressTracker.StartRun(jobId, pendingCount);
                runStarted = true;

                logger.LogInformation(
                    "Starting fingerprint run {RunId} with {PendingCount} pending tracks",
                    jobId, pendingCount);

                while (!ct.IsCancellationRequested)
                {
                    var processed = await ProcessNextBatchAsync(jobId, ct);
                    if (processed == 0) break;
                }

                progressTracker.CompleteRun(jobId);
                var wasCancelled = ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested;

                if (!wasCancelled)
                {
                    try
                    {
                        logger.LogInformation("Running duplicate detection after fingerprint run {RunId}", jobId);
                        await duplicateDetectionService.DetectDuplicatesAsync(stoppingToken);
                    }
                    catch (Exception dedupEx) when (dedupEx is not OperationCanceledException)
                    {
                        logger.LogWarning(dedupEx, "Duplicate detection failed after fingerprint run {RunId}", jobId);
                    }
                }

                jobManager.SignalComplete(jobId, wasCancelled);

                if (wasCancelled)
                    logger.LogInformation("Fingerprint run {RunId} cancelled via cancel endpoint", jobId);
                else
                    logger.LogInformation("Fingerprint run {RunId} complete", jobId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalComplete(jobId, cancelled: true);
                logger.LogInformation("Fingerprint run {RunId} stopped with application", jobId);
            }
            catch (Exception ex)
            {
                if (runStarted) progressTracker.CompleteRun(jobId);
                jobManager.SignalFailed(jobId);
                logger.LogError(ex, "Fingerprint run {RunId} failed", jobId);
                await Task.Delay(TimeSpan.FromSeconds(opts.FingerprintIdleDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task<int> ProcessNextBatchAsync(Guid runId, CancellationToken ct)
    {
        var opts = options.Value;

        List<(int Id, string SourcePath)> batch;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            // Background service: bypass the per-user query filter. Skip synthetic (demo) rows
            // because they have no real file to fingerprint.
            var query = db.Songs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
                .Where(s => s.Fingerprint == null || s.Fingerprint == string.Empty);

            if (_permanentlyFailed.Count > 0)
                query = query.Where(s => !_permanentlyFailed.Contains(s.Id));

            batch = await query
                .OrderBy(s => s.Id)
                .Take(opts.FingerprintBatchSize)
                .Select(s => new { s.Id, s.SourcePath })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(s => (s.Id, s.SourcePath)).ToList(), ct);
        }

        if (batch.Count == 0) return 0;

        var semaphore = new SemaphoreSlim(opts.FingerprintConcurrency, opts.FingerprintConcurrency);
        var results = new List<(int Id, FpcalcResult? Result)>();
        var resultsLock = new object();

        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = opts.FingerprintConcurrency,
                CancellationToken = ct
            },
            async (item, token) =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    var result = await fpcalcService.GetFingerprintAsync(item.SourcePath, token);
                    lock (resultsLock)
                    {
                        results.Add((item.Id, result));
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

        using var scope2 = scopeFactory.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ids = results.Select(r => r.Id).ToList();
        var songs = await db2.Songs
            .IgnoreQueryFilters()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        foreach (var (id, result) in results)
        {
            if (!songs.TryGetValue(id, out var song)) continue;

            if (result is not null)
            {
                song.Fingerprint = result.Fingerprint;
                song.DurationSeconds = result.DurationSeconds;
                progressTracker.IncrementFingerprinted();
            }
            else
            {
                _permanentlyFailed.Add(id);
                progressTracker.IncrementFailed();
                logger.LogWarning("fpcalc returned null for song {Id} ({Path})", id, song.SourcePath);
            }
        }

        await db2.SaveChangesAsync(ct);

        var fingerprintedIds = results
            .Where(r => r.Result is not null)
            .Select(r => r.Id)
            .ToList();
        if (fingerprintedIds.Count > 0)
        {
            enrichmentChannel.EnqueueRange(fingerprintedIds);
            logger.LogDebug("Enqueued {Count} fingerprinted songs for enrichment", fingerprintedIds.Count);
        }

        var state = progressTracker.GetCurrent();
        if (state is { Processed: var processed })
        {
            logger.LogInformation(
                "Fingerprint {RunId}: {Processed}/{Total} processed",
                runId, processed, state.TotalTracks);
        }

        return batch.Count;
    }

    /// <summary>Idle delay that returns false if the app is shutting down.</summary>
    private static async Task<bool> DelayIdleAsync(int seconds, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<int> CountPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var query = db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .Where(s => s.Fingerprint == null || s.Fingerprint == string.Empty);

        if (_permanentlyFailed.Count > 0)
            query = query.Where(s => !_permanentlyFailed.Contains(s.Id));

        return await query.CountAsync(ct);
    }
}
