using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Runs <see cref="AlbumCompletionSweep"/> on a slow interval. Deliberately its own service rather
/// than a rider on <c>DownloadBackgroundService</c>: that loop ticks every few seconds and backs off
/// adaptively, which would make this feature's throttle — the whole point of it — unpredictable.
/// <para>
/// The gating lives inside the sweep, so this loop keeps ticking (cheaply) even when the feature is
/// off; flipping the runtime toggle takes effect on the next tick with no restart.
/// </para>
/// </summary>
public class AlbumCompletionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<MusicEnricherOptions> options,
    ILogger<AlbumCompletionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sweep = scope.ServiceProvider.GetRequiredService<AlbumCompletionSweep>();
                await sweep.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Album completion sweep failed");
            }

            var minutes = Math.Clamp(options.CurrentValue.AlbumCompletionSweepIntervalMinutes, 1, 1440);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
