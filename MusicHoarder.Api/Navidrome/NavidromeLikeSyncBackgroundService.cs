using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Navidrome;

/// <summary>
/// Drives the two-way like sync: a periodic full reconcile sweep (both directions) plus a worker that
/// drains <see cref="NavidromeLikeSyncChannel"/> for near-immediate MH → Navidrome pushes after a
/// toggle. A plain hosted service, not a JobManager step — like sync is a light background side-effect
/// that must never hold or wait on the one-job-at-a-time pipeline locks. Entirely inert unless
/// <see cref="NavidromeOptions.IsConfigured"/>.
/// </summary>
public sealed class NavidromeLikeSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    NavidromeLikeSyncChannel channel,
    IOptionsMonitor<NavidromeOptions> options,
    ILogger<NavidromeLikeSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(RunSweepLoopAsync(stoppingToken), RunWorkerAsync(stoppingToken));
    }

    private async Task RunSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.IsConfigured)
                {
                    using var scope = scopeFactory.CreateScope();
                    var reconciler = scope.ServiceProvider.GetRequiredService<NavidromeLikeReconciler>();
                    await reconciler.ReconcileAllAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Navidrome like reconcile sweep failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.ReconcileIntervalSeconds), ct);
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
                if (!options.CurrentValue.IsConfigured)
                    continue; // config may flip at runtime; drain quietly when off

                using var scope = scopeFactory.CreateScope();
                var reconciler = scope.ServiceProvider.GetRequiredService<NavidromeLikeReconciler>();
                await reconciler.PushSongAsync(songId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Navidrome like push failed for song {SongId}", songId);
            }
            finally
            {
                channel.MarkProcessed(songId);
            }
        }
    }
}
