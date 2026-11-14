using Microsoft.Extensions.Options;
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
    ILogger<ArtistMonitorBackgroundService> logger) : BackgroundService
{
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
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sweep = scope.ServiceProvider.GetRequiredService<ArtistMonitorSweep>();
                await sweep.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Artist monitor sweep failed");
            }

            var minutes = Math.Clamp(options.CurrentValue.ArtistMonitorSweepIntervalMinutes, 1, 1440);
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
