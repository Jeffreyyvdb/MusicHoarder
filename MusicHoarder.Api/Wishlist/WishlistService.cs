using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Wishlist;

/// <summary>
/// Result of registering a source / running a sync: how many new wishlist items were added.
/// </summary>
public record WishlistSyncResult(int SourceId, int Added, int AlreadyPresent);

public interface IWishlistService
{
    /// <summary>
    /// Creates (or updates) a wishlist source for the owner and snapshots its current tracks
    /// into Pending wishlist items now. Snapshotting a large library is long-running — prefer
    /// <see cref="CreateOrUpdateSourceAsync"/> + a background <see cref="SyncSourceAsync"/> off the request path.
    /// </summary>
    Task<WishlistSyncResult> AddSourceAsync(
        Guid ownerId, WishlistSourceType type, string? playlistId, bool autoSync, CancellationToken ct);

    /// <summary>
    /// Creates or updates just the source row (fast: one optional metadata lookup, no track
    /// snapshot) and returns it. Callers then run <see cref="SyncSourceAsync"/> in the background.
    /// </summary>
    Task<WishlistSource> CreateOrUpdateSourceAsync(
        Guid ownerId, WishlistSourceType type, string? playlistId, bool autoSync, CancellationToken ct);

    /// <summary>
    /// Pages the source's current tracks and appends any not already on the owner's wishlist as
    /// Pending items (deduped by the owner's Spotify/Deezer track ids). Updates
    /// <see cref="WishlistSource.LastSyncedAtUtc"/>.
    /// </summary>
    /// <param name="maxPages">
    /// When set, stop after this many Spotify pages (50 tracks each) instead of paging the whole source.
    /// Used by the fast Liked-Songs poll, which only needs the newest-first first page(s). Null = full sweep.
    /// Ignored for Deezer sources (a discover playlist is always paged to completion).
    /// </param>
    Task<WishlistSyncResult> SyncSourceAsync(
        Guid ownerId, WishlistSource source, CancellationToken ct, int? maxPages = null);
}

public class WishlistService(
    MusicHoarderDbContext db,
    ISpotifyApiService spotifyApi,
    IDeezerCatalogService deezer,
    ISpotifyIsrcResolver isrcResolver,
    ILogger<WishlistService> logger) : IWishlistService
{
    private const int Page = 50;

    public async Task<WishlistSyncResult> AddSourceAsync(
        Guid ownerId, WishlistSourceType type, string? playlistId, bool autoSync, CancellationToken ct)
    {
        var source = await CreateOrUpdateSourceAsync(ownerId, type, playlistId, autoSync, ct);
        return await SyncSourceAsync(ownerId, source, ct);
    }

    public async Task<WishlistSource> CreateOrUpdateSourceAsync(
        Guid ownerId, WishlistSourceType type, string? playlistId, bool autoSync, CancellationToken ct)
    {
        if (type == WishlistSourceType.Playlist && string.IsNullOrWhiteSpace(playlistId))
            throw new InvalidOperationException("A playlistId is required for a playlist source.");
        if (type == WishlistSourceType.DeezerPlaylist && string.IsNullOrWhiteSpace(playlistId))
            throw new InvalidOperationException("A deezerPlaylistId is required for a Deezer playlist source.");

        // The single provider playlist id lands in exactly one of the typed columns (matches the unique
        // indexes: Spotify playlists key on SpotifyPlaylistId, Deezer on DeezerPlaylistId).
        var spotifyPlaylistId = type == WishlistSourceType.Playlist ? playlistId : null;
        var deezerPlaylistId = type == WishlistSourceType.DeezerPlaylist ? playlistId : null;

        var (name, imageUrl) = await ResolveSourceMetadataAsync(type, playlistId, ct);

        var source = await db.WishlistSources
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId
                && s.SourceType == type
                && s.SpotifyPlaylistId == spotifyPlaylistId
                && s.DeezerPlaylistId == deezerPlaylistId, ct);

        if (source is null)
        {
            source = new WishlistSource
            {
                OwnerUserId = ownerId,
                SourceType = type,
                SpotifyPlaylistId = spotifyPlaylistId,
                DeezerPlaylistId = deezerPlaylistId,
                Name = name,
                ImageUrl = imageUrl,
                AutoSync = autoSync,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.WishlistSources.Add(source);
        }
        else
        {
            source.AutoSync = autoSync;
            source.Name = name;
            source.ImageUrl = imageUrl;
        }

        await db.SaveChangesAsync(ct);
        return source;
    }

    public Task<WishlistSyncResult> SyncSourceAsync(
        Guid ownerId, WishlistSource source, CancellationToken ct, int? maxPages = null) =>
        source.SourceType == WishlistSourceType.DeezerPlaylist
            ? SyncDeezerSourceAsync(ownerId, source, ct)
            : SyncSpotifySourceAsync(ownerId, source, ct, maxPages);

    private async Task<WishlistSyncResult> SyncSpotifySourceAsync(
        Guid ownerId, WishlistSource source, CancellationToken ct, int? maxPages)
    {
        // Dedupe against everything already on the owner's wishlist (any source). The set is also
        // seeded as we insert, so a track that recurs within the same fetch counts as already-present.
        var existingIds = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.SpotifyTrackId != null)
            .Select(w => w.SpotifyTrackId!)
            .ToListAsync(ct);
        var seen = new HashSet<string>(existingIds, StringComparer.Ordinal);

        var added = 0;
        var alreadyPresent = 0;
        var offset = 0;
        var pagesFetched = 0;

        // Page through Spotify and persist each page as we go. A large library (thousands of liked
        // songs) is dozens of sequential Spotify calls — far longer than an HTTP request should run,
        // so callers snapshot in the background. Per-page SaveChanges makes items appear progressively
        // and survive a cancellation mid-snapshot instead of rolling the whole batch back.
        while (true)
        {
            var (items, total) = await FetchSpotifyPageAsync(source, offset, ct);
            if (items.Count == 0) break;

            var now = DateTime.UtcNow;
            foreach (var track in items)
            {
                if (string.IsNullOrEmpty(track.SpotifyId)) continue;
                if (!seen.Add(track.SpotifyId))
                {
                    alreadyPresent++;
                    continue;
                }

                db.WishlistItems.Add(new WishlistItem
                {
                    OwnerUserId = ownerId,
                    WishlistSourceId = source.Id,
                    SpotifyTrackId = track.SpotifyId,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    Isrc = track.Isrc,
                    DurationMs = track.DurationMs,
                    AlbumArt = track.AlbumArt,
                    SpotifyAddedAtUtc = track.AddedAt == default ? null : track.AddedAt,
                    Status = WishlistItemStatus.Pending,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
                added++;
            }

            await db.SaveChangesAsync(ct);

            offset += items.Count;
            if (offset >= total) break;

            // Fast poll: stop after the bounded number of newest-first pages. The next full sweep
            // (no cap) reconciles anything older this shallow window didn't reach.
            if (maxPages is { } cap && ++pagesFetched >= cap) break;
        }

        source.LastSyncedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (added > 0)
            logger.LogInformation("Wishlist source {SourceId} ({Name}): added {Added} new items", source.Id, LogSanitizer.ForLog(source.Name), added);

        return new WishlistSyncResult(source.Id, added, alreadyPresent);
    }

    private async Task<WishlistSyncResult> SyncDeezerSourceAsync(
        Guid ownerId, WishlistSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.DeezerPlaylistId))
            return new WishlistSyncResult(source.Id, 0, 0);

        var playlist = await deezer.GetPlaylistAsync(source.DeezerPlaylistId, ct);

        // Skip-if-unchanged: Deezer's tracklist checksum is stable while the playlist's tracks are, so a
        // matching stored checksum means there's nothing new to page.
        if (playlist is not null
            && !string.IsNullOrEmpty(playlist.Checksum)
            && string.Equals(playlist.Checksum, source.RemoteChecksum, StringComparison.Ordinal))
        {
            source.LastSyncedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new WishlistSyncResult(source.Id, 0, 0);
        }

        // Dedupe against the owner's existing wishlist by both provider keys: a track already present via
        // Deezer OR via a resolved Spotify id must not be re-added.
        var existing = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId)
            .Select(w => new { w.SpotifyTrackId, w.DeezerTrackId })
            .ToListAsync(ct);
        var seenDeezer = new HashSet<string>(
            existing.Where(e => e.DeezerTrackId != null).Select(e => e.DeezerTrackId!), StringComparer.Ordinal);
        var seenSpotify = new HashSet<string>(
            existing.Where(e => e.SpotifyTrackId != null).Select(e => e.SpotifyTrackId!), StringComparer.Ordinal);

        var tracksResult = await deezer.GetPlaylistTracksAsync(source.DeezerPlaylistId, ct: ct);
        var tracks = tracksResult.Tracks;

        var added = 0;
        var alreadyPresent = 0;
        var now = DateTime.UtcNow;

        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(track.Id)) continue;

            if (!seenDeezer.Add(track.Id))
            {
                alreadyPresent++;
                continue;
            }

            // Hydrate the track for its ISRC (the tracklist payload omits it), then best-effort resolve a
            // Spotify id so this row shares the owner's cross-provider dedupe key.
            var detail = await deezer.LookupByIdAsync(track.Id, ct);
            var isrc = detail?.Isrc;
            var spotifyId = await isrcResolver.ResolveTrackIdByIsrcAsync(isrc, ct);

            if (spotifyId is not null && !seenSpotify.Add(spotifyId))
            {
                alreadyPresent++;
                continue;
            }

            db.WishlistItems.Add(new WishlistItem
            {
                OwnerUserId = ownerId,
                WishlistSourceId = source.Id,
                SpotifyTrackId = spotifyId,
                DeezerTrackId = track.Id,
                Title = track.Title,
                Artist = track.Artist,
                Album = track.Album,
                Isrc = isrc,
                DurationMs = track.DurationMs,
                AlbumArt = track.CoverUrl,
                SpotifyAddedAtUtc = null,
                Status = WishlistItemStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            added++;

            // Persist incrementally so a long playlist snapshot appears progressively and survives a
            // cancellation mid-run (mirrors the Spotify per-page behavior).
            await db.SaveChangesAsync(ct);
        }

        // Only advance the skip-if-unchanged checksum when the whole tracklist was fetched. A mid-run
        // page failure leaves the inserted items persisted but the checksum unset, so the next sync
        // retries the missing tail instead of the checksum-skip permanently hiding it.
        if (tracksResult.IsComplete)
            source.RemoteChecksum = playlist?.Checksum;
        source.LastSyncedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (added > 0)
            logger.LogInformation("Wishlist source {SourceId} ({Name}): added {Added} new Deezer items", source.Id, LogSanitizer.ForLog(source.Name), added);

        return new WishlistSyncResult(source.Id, added, alreadyPresent);
    }

    private async Task<(IReadOnlyList<SpotifyTrackItem> Items, int Total)> FetchSpotifyPageAsync(
        WishlistSource source, int offset, CancellationToken ct)
    {
        if (source.SourceType == WishlistSourceType.LikedSongs)
        {
            var page = await spotifyApi.GetLikedSongsAsync(offset, Page, ct);
            return (page.Items, page.Total);
        }

        if (!string.IsNullOrWhiteSpace(source.SpotifyPlaylistId))
        {
            var page = await spotifyApi.GetPlaylistTracksAsync(source.SpotifyPlaylistId, offset, Page, ct);
            return (page.Items, page.Total);
        }

        return (Array.Empty<SpotifyTrackItem>(), 0);
    }

    private async Task<(string Name, string? ImageUrl)> ResolveSourceMetadataAsync(
        WishlistSourceType type, string? playlistId, CancellationToken ct)
    {
        if (type == WishlistSourceType.LikedSongs)
            return ("Liked Songs", null);

        if (type == WishlistSourceType.DeezerPlaylist)
        {
            var deezerPlaylist = string.IsNullOrWhiteSpace(playlistId)
                ? null
                : await deezer.GetPlaylistAsync(playlistId, ct);
            return deezerPlaylist is null
                ? (playlistId ?? "Playlist", null)
                : (deezerPlaylist.Title, deezerPlaylist.CoverUrl);
        }

        // Spotify playlist: prefer the friendly name + cover from the user's own playlist list, then fall
        // back to a direct playlist fetch (editorial/shared playlists aren't in /me/playlists), then the id.
        var playlists = await spotifyApi.GetPlaylistsAsync(ct);
        var match = playlists.Items.FirstOrDefault(p => p.SpotifyId == playlistId);
        if (match is not null)
            return (match.Name, match.ImageUrl);

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            var lookup = await spotifyApi.GetPlaylistAsync(playlistId, ct);
            if (lookup.Found && lookup.Playlist is not null)
                return (lookup.Playlist.Name, lookup.Playlist.ImageUrl);
        }

        return (playlistId ?? "Playlist", null);
    }
}
