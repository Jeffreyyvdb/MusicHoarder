using Microsoft.EntityFrameworkCore;
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
    /// Creates (or updates) a wishlist source for the owner and snapshots its current Spotify tracks
    /// into Pending wishlist items now. Snapshotting a large library is long-running — prefer
    /// <see cref="CreateOrUpdateSourceAsync"/> + a background <see cref="SyncSourceAsync"/> off the request path.
    /// </summary>
    Task<WishlistSyncResult> AddSourceAsync(
        Guid ownerId, WishlistSourceType type, string? playlistId, bool autoSync, CancellationToken ct);

    /// <summary>
    /// Creates or updates just the source row (fast: one optional Spotify metadata lookup, no track
    /// snapshot) and returns it. Callers then run <see cref="SyncSourceAsync"/> in the background.
    /// </summary>
    Task<WishlistSource> CreateOrUpdateSourceAsync(
        Guid ownerId, WishlistSourceType type, string? playlistId, bool autoSync, CancellationToken ct);

    /// <summary>
    /// Pages the source's current Spotify tracks and appends any not already on the owner's wishlist as
    /// Pending items (deduped by the unique <c>(OwnerUserId, SpotifyTrackId)</c> index). Updates
    /// <see cref="WishlistSource.LastSyncedAtUtc"/>.
    /// </summary>
    Task<WishlistSyncResult> SyncSourceAsync(Guid ownerId, WishlistSource source, CancellationToken ct);
}

public class WishlistService(
    MusicHoarderDbContext db,
    ISpotifyApiService spotifyApi,
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

        // Normalize: LikedSongs never carries a playlist id (matches the unique index).
        var normalizedPlaylistId = type == WishlistSourceType.LikedSongs ? null : playlistId;

        var (name, imageUrl) = await ResolveSourceMetadataAsync(type, normalizedPlaylistId, ct);

        var source = await db.WishlistSources
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId
                && s.SourceType == type
                && s.SpotifyPlaylistId == normalizedPlaylistId, ct);

        if (source is null)
        {
            source = new WishlistSource
            {
                OwnerUserId = ownerId,
                SourceType = type,
                SpotifyPlaylistId = normalizedPlaylistId,
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

    public async Task<WishlistSyncResult> SyncSourceAsync(Guid ownerId, WishlistSource source, CancellationToken ct)
    {
        // Dedupe against everything already on the owner's wishlist (any source). The set is also
        // seeded as we insert, so a track that recurs within the same fetch counts as already-present.
        var existingIds = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId)
            .Select(w => w.SpotifyTrackId)
            .ToListAsync(ct);
        var seen = new HashSet<string>(existingIds, StringComparer.Ordinal);

        var added = 0;
        var alreadyPresent = 0;
        var offset = 0;

        // Page through Spotify and persist each page as we go. A large library (thousands of liked
        // songs) is dozens of sequential Spotify calls — far longer than an HTTP request should run,
        // so callers snapshot in the background. Per-page SaveChanges makes items appear progressively
        // and survive a cancellation mid-snapshot instead of rolling the whole batch back.
        while (true)
        {
            var (items, total) = await FetchPageAsync(source, offset, ct);
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
        }

        source.LastSyncedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (added > 0)
            logger.LogInformation("Wishlist source {SourceId} ({Name}): added {Added} new items", source.Id, LogSanitizer.ForLog(source.Name), added);

        return new WishlistSyncResult(source.Id, added, alreadyPresent);
    }

    private async Task<(IReadOnlyList<SpotifyTrackItem> Items, int Total)> FetchPageAsync(
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

        // Look the playlist up in the user's playlist list to get a friendly name + cover.
        var playlists = await spotifyApi.GetPlaylistsAsync(ct);
        var match = playlists.Items.FirstOrDefault(p => p.SpotifyId == playlistId);
        return match is null
            ? (playlistId ?? "Playlist", null)
            : (match.Name, match.ImageUrl);
    }
}
