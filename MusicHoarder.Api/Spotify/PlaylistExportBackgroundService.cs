using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Periodically mirrors the owner's Spotify Liked Songs + playlists to on-disk <c>.m3u8</c> files
/// (see <see cref="IPlaylistExportService"/>). Picks up newly-built tracks each run, so a playlist's
/// coverage grows as more of its tracks land in the library.
/// </summary>
public class PlaylistExportBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MusicEnricherOptions> options,
    ILogger<PlaylistExportBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.EnablePlaylistExport)
        {
            logger.LogInformation("Playlist export is disabled (MusicEnricher:EnablePlaylistExport = false)");
            return;
        }

        var intervalMinutes = Math.Max(0, opts.PlaylistExportIntervalMinutes);
        if (intervalMinutes == 0)
        {
            logger.LogInformation("Playlist export background run is disabled (MusicEnricher:PlaylistExportIntervalMinutes = 0)");
            return;
        }

        var period = TimeSpan.FromMinutes(intervalMinutes);

        try
        {
            // Stagger after boot so the first run doesn't collide with startup migrations / warm-up.
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                if (!await IsSpotifyConnectedAsync(scope, stoppingToken))
                {
                    await Task.Delay(period, stoppingToken);
                    continue;
                }

                var export = scope.ServiceProvider.GetRequiredService<IPlaylistExportService>();
                await export.RunExportAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SpotifyNotConnectedException)
            {
                // disconnected during run
            }
            catch (SpotifyRateLimitException ex)
            {
                logger.LogWarning(ex, "Spotify rate limited during playlist export; will retry on next interval");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Playlist export background run failed");
            }

            try
            {
                await Task.Delay(period, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task<bool> IsSpotifyConnectedAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        return await db.SpotifySettings
            .AsNoTracking()
            .AnyAsync(s => !string.IsNullOrEmpty(s.AccessToken) && !string.IsNullOrEmpty(s.RefreshToken), ct);
    }
}
