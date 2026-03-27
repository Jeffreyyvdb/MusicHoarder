using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Periodically syncs Spotify liked songs against the local library and persists per-track matches (BRINK-69).
/// </summary>
public class SpotifyLibraryMatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<SpotifyOptions> spotifyOptions,
    ILogger<SpotifyLibraryMatchBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(0, spotifyOptions.Value.LibraryMatchSyncIntervalMinutes);
        if (intervalMinutes == 0)
        {
            logger.LogInformation(
                "Spotify library match background sync is disabled (Spotify:LibraryMatchSyncIntervalMinutes = 0)");
            return;
        }

        var period = TimeSpan.FromMinutes(intervalMinutes);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
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

                var comparison = scope.ServiceProvider.GetRequiredService<ISpotifyLibraryComparisonService>();
                await comparison.SyncLikedSongsMatchesAsync(stoppingToken);
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
                logger.LogWarning(ex, "Spotify rate limited during library match sync; will retry on next interval");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Spotify library match background sync failed");
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
        var row = await db.SpotifySettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return row is { IsConnected: true };
    }
}
