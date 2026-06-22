using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Wishlist;

/// <summary>
/// Keeps the wishlist current with Spotify on two cadences from a single loop. Every tick runs a cheap
/// near-real-time <em>fast poll</em> of auto-synced Liked-Songs sources (first page(s) only, newest-first),
/// so a freshly liked song lands on the wishlist within <c>LikedSongsFastPollSeconds</c> instead of waiting
/// the full interval; once per <c>WishlistSyncIntervalMinutes</c> the tick instead runs a <em>full sweep</em>
/// of every auto-synced source (playlists included, paged to completion) to reconcile playlist edits and any
/// older likes the shallow window missed. New tracks are appended as Pending items; when any were added and
/// auto-download is enabled it kicks the download worker directly so it doesn't wait out its idle poll.
/// Mirrors the structure of <see cref="SpotifyLibraryMatchBackgroundService"/>.
/// </summary>
public class WishlistSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOwnerLookupService ownerLookup,
    JobManager jobManager,
    IOptions<SpotifyOptions> spotifyOptions,
    IOptions<MusicEnricherOptions> enricherOptions,
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

        var fullSweepPeriod = TimeSpan.FromMinutes(intervalMinutes);
        var fastPollSeconds = Math.Max(0, spotifyOptions.Value.LikedSongsFastPollSeconds);
        // Tick at the fast-poll cadence when enabled; otherwise the loop just does full sweeps.
        var tickPeriod = fastPollSeconds > 0 ? TimeSpan.FromSeconds(fastPollSeconds) : fullSweepPeriod;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var lastFullSweepUtc = DateTime.MinValue; // first tick runs a full sweep

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var doFull = fastPollSeconds == 0
                    || lastFullSweepUtc == DateTime.MinValue
                    || now - lastFullSweepUtc >= fullSweepPeriod;

                await SyncOnceAsync(full: doFull, stoppingToken);
                if (doFull) lastFullSweepUtc = now;
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
                logger.LogWarning(ex, "Spotify rate limited during wishlist sync; will retry on next tick");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wishlist sync failed");
            }

            try
            {
                await Task.Delay(tickPeriod, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one sync sweep and returns how many new items were added, kicking the download worker if any
    /// were. A full sweep pages every auto-synced source to completion; a fast poll touches only
    /// Liked-Songs sources and stops after <see cref="SpotifyOptions.WishlistFastPollMaxPages"/>
    /// newest-first pages. Internal so a single sweep can be unit-tested without the polling loop.
    /// </summary>
    internal async Task<int> SyncOnceAsync(bool full, CancellationToken ct)
    {
        var ownerId = ownerLookup.OwnerUserId;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        // IgnoreQueryFilters: this runs in a hosted-service scope where ICurrentUserAccessor has no
        // HTTP user, so the multi-tenant filter would resolve to OwnerUserId == Guid.Empty and hide the
        // owner's settings row — making this gate always-false and silently disabling auto-sync.
        var connected = await db.SpotifySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(s => !string.IsNullOrEmpty(s.AccessToken) && !string.IsNullOrEmpty(s.RefreshToken), ct);
        if (!connected) return 0;

        var sourcesQuery = db.WishlistSources
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.AutoSync);
        if (!full)
            sourcesQuery = sourcesQuery.Where(s => s.SourceType == WishlistSourceType.LikedSongs);

        var sources = await sourcesQuery.ToListAsync(ct);
        if (sources.Count == 0) return 0;

        int? maxPages = full ? null : Math.Max(1, spotifyOptions.Value.WishlistFastPollMaxPages);

        var wishlist = scope.ServiceProvider.GetRequiredService<IWishlistService>();
        var added = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await wishlist.SyncSourceAsync(ownerId, source, ct, maxPages);
                added += result.Added;
            }
            catch (SpotifyRateLimitException)
            {
                throw; // bubble to the outer handler; retry the whole sweep next tick
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Wishlist sync failed for source {SourceId} ({Name})", source.Id, LogSanitizer.ForLog(source.Name));
            }
        }

        if (added > 0) MaybeKickDownload(added);
        return added;
    }

    /// <summary>
    /// Wake the download worker immediately when a sync added items, instead of letting it wait out its
    /// idle poll. Gated by both download flags — mirroring the worker's own auto-sweep gate — so it never
    /// fires (and logs/SignalCompletes a no-op) when the feature or auto-download is off.
    /// </summary>
    private void MaybeKickDownload(int added)
    {
        var opts = enricherOptions.Value;
        if (!opts.EnableWishlistDownloads || !opts.AutoDownloadWishlist) return;

        if (jobManager.TryStartJob(JobType.Download, out var jobId, out _))
            logger.LogInformation("Wishlist sync added {Added} item(s); kicked download {JobId}", added, jobId);
    }
}
