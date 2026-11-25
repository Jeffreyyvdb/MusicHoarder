using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork;
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
    ILogger<AlbumCompletionBackgroundService> logger,
    BackgroundWorkStatus? status = null) : BackgroundService
{
    internal const string Source = "album-completion";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Clamp(options.CurrentValue.AlbumCompletionSweepIntervalMinutes, 1, 1440);
            var next = DateTime.UtcNow.AddMinutes(minutes);
            status?.Working(BackgroundJobIds.AlbumFillAndFollows, Source);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sweep = scope.ServiceProvider.GetRequiredService<AlbumCompletionSweep>();
                var result = await sweep.SweepAsync(stoppingToken);
                if (result.IdleReason == AlbumCompletionSweepResult.IdleAtPendingCeiling)
                {
                    status?.Waiting(
                        BackgroundJobIds.AlbumFillAndFollows,
                        "Album completion waits for the downloader to work through what is queued.",
                        next,
                        Source);
                }
                else
                {
                    status?.Idle(BackgroundJobIds.AlbumFillAndFollows, next, result.TracksQueued > 0, Source);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Album completion sweep failed");
                status?.Failing(BackgroundJobIds.AlbumFillAndFollows, ex, next, Source);
            }

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
