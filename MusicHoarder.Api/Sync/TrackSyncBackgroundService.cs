using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Sync;

/// <summary>
/// Push-side sync worker: consumes <see cref="TrackSyncChannel"/> (fed per-track by the library
/// builder's post-build hook) with small bounded concurrency, plus a periodic sweep that finds
/// anything the hook missed — the startup backlog (first-time seeding pushes the whole built
/// library), retryable failures, crash-stale rows, and fingerprint re-arms after local upgrades.
/// A plain hosted service, deliberately NOT a JobManager step: a long-tailed network side-effect
/// must never hold or wait on the one-job-at-a-time pipeline locks.
/// </summary>
public class TrackSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    TrackSyncChannel channel,
    IOptionsMonitor<SyncOptions> options,
    ILogger<TrackSyncBackgroundService> logger) : BackgroundService
{
    private const int SweepBatchLimit = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, Math.Max(1, options.CurrentValue.PushConcurrency))
            .Select(_ => RunWorkerAsync(stoppingToken))
            .ToArray();
        await Task.WhenAll([RunSweepLoopAsync(stoppingToken), .. workers]);
    }

    private async Task RunSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.IsPushConfigured)
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<TrackSyncProcessor>();
                    var candidates = await processor.FindSweepCandidatesAsync(SweepBatchLimit, ct);
                    if (candidates.Count > 0)
                    {
                        logger.LogInformation("Sync sweep enqueued {Count} track(s)", candidates.Count);
                        channel.EnqueueRange(candidates);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sync sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.SweepIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        await foreach (var songId in channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Config may flip at runtime; drain quietly when push is off.
                if (!options.CurrentValue.IsPushConfigured)
                    continue;

                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<TrackSyncProcessor>();
                await processor.ProcessSongAsync(songId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sync worker failed processing song {SongId}", songId);
            }
            finally
            {
                channel.MarkProcessed(songId);
            }
        }
    }
}
