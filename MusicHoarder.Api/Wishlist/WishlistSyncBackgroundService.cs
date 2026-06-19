using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Wishlist;

/// <summary>
/// Periodically polls every auto-synced wishlist source for newly liked / newly added tracks and
/// appends them as Pending wishlist items, which the download worker then picks up. Mirrors the
/// structure of <see cref="SpotifyLibraryMatchBackgroundService"/>.
/// </summary>
public class WishlistSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOwnerLookupService ownerLookup,
    IOptions<SpotifyOptions> spotifyOptions,
    ILogger<WishlistSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(0, spotifyOptions.Value.WishlistSyncIntervalMinutes);
        if (intervalMinutes == 0)
        {
            logger.LogInformation("Wishlist sync is disabled (Spotify:WishlistSyncIntervalMinutes = 0)");
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
                await SyncOnceAsync(stoppingToken);
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
                logger.LogWarning(ex, "Spotify rate limited during wishlist sync; will retry on next interval");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wishlist sync failed");
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

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        var ownerId = ownerLookup.OwnerUserId;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var connected = await db.SpotifySettings
            .AsNoTracking()
            .AnyAsync(s => !string.IsNullOrEmpty(s.AccessToken) && !string.IsNullOrEmpty(s.RefreshToken), ct);
        if (!connected) return;

        var sources = await db.WishlistSources
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.AutoSync)
            .ToListAsync(ct);
        if (sources.Count == 0) return;

        var wishlist = scope.ServiceProvider.GetRequiredService<IWishlistService>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await wishlist.SyncSourceAsync(ownerId, source, ct);
            }
            catch (SpotifyRateLimitException)
            {
                throw; // bubble to the outer handler; retry the whole sweep next interval
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Wishlist sync failed for source {SourceId} ({Name})", source.Id, LogSanitizer.ForLog(source.Name));
            }
        }
    }
}
