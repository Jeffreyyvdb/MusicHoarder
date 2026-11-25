using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Artists;

/// <summary>
/// Runs <see cref="ArtistMonitorSweep"/> every <c>MusicEnricher:ArtistMonitorSweepIntervalMinutes</c>.
/// Each pass checks only the followed artists whose own timer is due
/// (<c>ArtistMonitorCheckIntervalHours</c>), a bounded number at a time, so a long follow list spreads
/// its catalog reads out instead of hitting Deezer all at once.
/// </summary>
public sealed class ArtistMonitorBackgroundService(
    IServiceScopeFactory scopes,
    IOptionsMonitor<MusicEnricherOptions> options,
    ILogger<ArtistMonitorBackgroundService> logger,
    BackgroundWorkStatus? status = null) : BackgroundService
{
    internal const string Source = "artist-monitor";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.EnableArtistMonitor)
        {
            logger.LogInformation("Artist monitor disabled (EnableArtistMonitor=false)");
            return;
        }

        // Let startup (migrations, the first scan) settle before reading catalogs.
        if (!await DelayAsync(TimeSpan.FromMinutes(2), stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Clamp(options.CurrentValue.ArtistMonitorSweepIntervalMinutes, 1, 1440);
            var next = DateTime.UtcNow.AddMinutes(minutes);
            status?.Working(BackgroundJobIds.AlbumFillAndFollows, Source);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sweep = scope.ServiceProvider.GetRequiredService<ArtistMonitorSweep>();
                var result = await sweep.SweepAsync(stoppingToken);
                status?.Idle(BackgroundJobIds.AlbumFillAndFollows, next, result.NewReleases > 0, Source);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Artist monitor sweep failed");
                status?.Failing(BackgroundJobIds.AlbumFillAndFollows, ex, next, Source);
            }

            if (!await DelayAsync(TimeSpan.FromMinutes(minutes), stoppingToken)) return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
