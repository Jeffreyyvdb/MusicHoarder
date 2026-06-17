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
    /// into Pending wishlist items now.
    /// </summary>
    Task<WishlistSyncResult> AddSourceAsync(
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

        return await SyncSourceAsync(ownerId, source, ct);
    }

    public async Task<WishlistSyncResult> SyncSourceAsync(Guid ownerId, WishlistSource source, CancellationToken ct)
    {
        var tracks = await FetchAllTracksAsync(source, ct);

        // Dedupe against everything already on the owner's wishlist (any source).
        var existingIds = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId)
            .Select(w => w.SpotifyTrackId)
            .ToListAsync(ct);
        var existing = new HashSet<string>(existingIds, StringComparer.Ordinal);

        var added = 0;
        var alreadyPresent = 0;
        var now = DateTime.UtcNow;
        var seenThisRun = new HashSet<string>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.SpotifyId)) continue;
            if (!seenThisRun.Add(track.SpotifyId)) continue; // duplicate within the same fetch
            if (existing.Contains(track.SpotifyId))
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

        source.LastSyncedAtUtc = now;
        await db.SaveChangesAsync(ct);

        if (added > 0)
            logger.LogInformation("Wishlist source {SourceId} ({Name}): added {Added} new items", source.Id, LogSanitizer.ForLog(source.Name), added);

        return new WishlistSyncResult(source.Id, added, alreadyPresent);
    }

    private async Task<List<SpotifyTrackItem>> FetchAllTracksAsync(WishlistSource source, CancellationToken ct)
    {
        var all = new List<SpotifyTrackItem>();

        if (source.SourceType == WishlistSourceType.LikedSongs)
        {
            var first = await spotifyApi.GetLikedSongsAsync(0, Page, ct);
            all.AddRange(first.Items);
            for (var offset = Page; offset < first.Total; offset += Page)
            {
                var page = await spotifyApi.GetLikedSongsAsync(offset, Page, ct);
                if (page.Items.Count == 0) break;
                all.AddRange(page.Items);
            }
        }
        else if (!string.IsNullOrWhiteSpace(source.SpotifyPlaylistId))
        {
            var first = await spotifyApi.GetPlaylistTracksAsync(source.SpotifyPlaylistId, 0, Page, ct);
            all.AddRange(first.Items);
            for (var offset = Page; offset < first.Total; offset += Page)
            {
                var page = await spotifyApi.GetPlaylistTracksAsync(source.SpotifyPlaylistId, offset, Page, ct);
                if (page.Items.Count == 0) break;
                all.AddRange(page.Items);
            }
        }

        return all;
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
