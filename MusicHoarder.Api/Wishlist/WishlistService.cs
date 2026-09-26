using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Import;
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
    /// Pending items (deduped by the owner's Spotify/Deezer track ids, or YouTube video ids). Updates
    /// <see cref="WishlistSource.LastSyncedAtUtc"/>.
    /// </summary>
    /// <param name="maxPages">
    /// When set, stop after this many Spotify pages (50 tracks each) instead of paging the whole source.
    /// Used by the fast Liked-Songs poll, which only needs the newest-first first page(s). Null = full sweep.
    /// Ignored for Deezer and YouTube sources (those playlists are always read to completion).
    /// </param>
    Task<WishlistSyncResult> SyncSourceAsync(
        Guid ownerId, WishlistSource source, CancellationToken ct, int? maxPages = null);
}

public class WishlistService(
    MusicHoarderDbContext db,
    ISpotifyApiService spotifyApi,
    IDeezerCatalogService deezer,
    ISpotifyIsrcResolver isrcResolver,
    IYouTubePlaylistReader youTubePlaylists,
    IYouTubeMetadataResolver youTubeVideos,
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
        if (type == WishlistSourceType.YouTubePlaylist && string.IsNullOrWhiteSpace(playlistId))
            throw new InvalidOperationException("A youTubePlaylistId is required for a YouTube playlist source.");

        // The single provider playlist id lands in exactly one of the typed columns (matches the unique
        // indexes: Spotify playlists key on SpotifyPlaylistId, Deezer on DeezerPlaylistId, YouTube on
        // YouTubePlaylistId).
        var spotifyPlaylistId = type == WishlistSourceType.Playlist ? playlistId : null;
        var deezerPlaylistId = type == WishlistSourceType.DeezerPlaylist ? playlistId : null;
        var youTubePlaylistId = type == WishlistSourceType.YouTubePlaylist ? playlistId?.Trim() : null;

        var (name, imageUrl) = await ResolveSourceMetadataAsync(type, playlistId, ct);

        var source = await db.WishlistSources
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId
                && s.SourceType == type
                && s.SpotifyPlaylistId == spotifyPlaylistId
                && s.DeezerPlaylistId == deezerPlaylistId
                && s.YouTubePlaylistId == youTubePlaylistId, ct);

        if (source is null)
        {
            source = new WishlistSource
            {
                OwnerUserId = ownerId,
                SourceType = type,
                SpotifyPlaylistId = spotifyPlaylistId,
                DeezerPlaylistId = deezerPlaylistId,
                YouTubePlaylistId = youTubePlaylistId,
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
        source.SourceType switch
        {
            WishlistSourceType.DeezerPlaylist => SyncDeezerSourceAsync(ownerId, source, ct),
            WishlistSourceType.YouTubePlaylist => SyncYouTubeSourceAsync(ownerId, source, ct),
            _ => SyncSpotifySourceAsync(ownerId, source, ct, maxPages),
        };

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
        // The remote list in order, for the source's playlist. Only a read that reached the end is
        // recorded: the fast poll's first pages are not the whole playlist.
        var remoteOrder = new List<string>();
        var complete = false;

        // Page through Spotify and persist each page as we go. A large library (thousands of liked
        // songs) is dozens of sequential Spotify calls — far longer than an HTTP request should run,
        // so callers snapshot in the background. Per-page SaveChanges makes items appear progressively
        // and survive a cancellation mid-snapshot instead of rolling the whole batch back.
        while (true)
        {
            var (items, total) = await FetchSpotifyPageAsync(source, offset, ct);
            if (items.Count == 0)
            {
                complete = true;
                break;
            }

            var now = DateTime.UtcNow;
            foreach (var track in items)
            {
                if (string.IsNullOrEmpty(track.SpotifyId)) continue;
                remoteOrder.Add(track.SpotifyId);
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
            if (offset >= total)
            {
                complete = true;
                break;
            }

            // Fast poll: stop after the bounded number of newest-first pages. The next full sweep
            // (no cap) reconciles anything older this shallow window didn't reach.
            if (maxPages is { } cap && ++pagesFetched >= cap) break;
        }

        if (complete)
        {
            var itemIdBySpotifyId = await db.WishlistItems
                .IgnoreQueryFilters()
                .Where(w => w.OwnerUserId == ownerId && w.SpotifyTrackId != null && remoteOrder.Contains(w.SpotifyTrackId))
                .Select(w => new { w.Id, w.SpotifyTrackId })
                .ToDictionaryAsync(w => w.SpotifyTrackId!, w => w.Id, StringComparer.Ordinal, ct);
            await RecordSourceTracksAsync(source, OrderedItemIds(remoteOrder, itemIdBySpotifyId), ct);
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
        // matching stored checksum means there's nothing new to page — unless the tracklist was never
        // recorded for the source's playlist, which needs one complete read.
        if (playlist is not null
            && source.TracksRecordedAtUtc is not null
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
        // Each remote track's key, in order: its Deezer id, plus the Spotify id when that is what an
        // existing item was deduplicated on.
        var remoteOrder = new List<(string DeezerId, string? SpotifyId)>();

        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(track.Id)) continue;

            if (!seenDeezer.Add(track.Id))
            {
                remoteOrder.Add((track.Id, null));
                alreadyPresent++;
                continue;
            }

            // Hydrate the track for its ISRC (the tracklist payload omits it), then best-effort resolve a
            // Spotify id so this row shares the owner's cross-provider dedupe key.
            var detail = await deezer.LookupByIdAsync(track.Id, ct);
            var isrc = detail?.Isrc;
            var spotifyId = await isrcResolver.ResolveTrackIdByIsrcAsync(isrc, ct);
            remoteOrder.Add((track.Id, spotifyId));

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
        {
            source.RemoteChecksum = playlist?.Checksum;
            await RecordDeezerSourceTracksAsync(ownerId, source, remoteOrder, ct);
        }
        source.LastSyncedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (added > 0)
            logger.LogInformation("Wishlist source {SourceId} ({Name}): added {Added} new Deezer items", source.Id, LogSanitizer.ForLog(source.Name), added);

        return new WishlistSyncResult(source.Id, added, alreadyPresent);
    }

    /// <summary>
    /// Appends the playlist's new videos. Each item downloads that exact video — its audio, and the
    /// clip itself as the song's music video, whatever the server's music-video default: the video is
    /// why it is on the list. The flat listing carries no channel, so each new video is probed once
    /// for its artist (and, for YouTube Music uploads, its album); only new videos cost a request.
    /// </summary>
    private async Task<WishlistSyncResult> SyncYouTubeSourceAsync(
        Guid ownerId, WishlistSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.YouTubePlaylistId))
            return new WishlistSyncResult(source.Id, 0, 0);

        var outcome = await youTubePlaylists.ReadAsync(source.YouTubePlaylistId, maxEntries: null, ct);
        if (!outcome.Ok)
            throw new InvalidOperationException(
                $"Could not read the YouTube playlist: {outcome.Hint ?? outcome.Detail ?? "no detail from yt-dlp"}");
        var playlist = outcome.Playlist!;

        // Dedupe by video against every row of the owner's wishlist, from any source. A video pasted by
        // hand before YouTubeVideoId existed carries its id only inside its SourceUrl.
        var existing = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && (w.YouTubeVideoId != null || w.SourceUrl != null))
            .Select(w => new { w.YouTubeVideoId, w.SourceUrl })
            .ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in existing)
        {
            if (row.YouTubeVideoId is { } videoId)
                seen.Add(videoId);
            else if (ImportUrlParser.TryParse(row.SourceUrl, out var kind, out var parsedId) && kind == ImportUrlKind.YouTube)
                seen.Add(parsedId);
        }

        var added = 0;
        var alreadyPresent = 0;
        foreach (var entry in playlist.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(entry.VideoId))
            {
                alreadyPresent++;
                continue;
            }

            // Re-check before the probe: a first snapshot is slow, and the periodic sweep can reach the
            // same playlist while it runs. The other run may have stored this video since `seen` was read,
            // and a probe is a YouTube request.
            if (await db.WishlistItems.IgnoreQueryFilters()
                    .AnyAsync(w => w.OwnerUserId == ownerId && w.YouTubeVideoId == entry.VideoId, ct))
            {
                alreadyPresent++;
                continue;
            }

            var watchUrl = ImportUrlParser.YouTubeWatchUrl(entry.VideoId);
            var video = await DescribeVideoAsync(entry, watchUrl, ct);
            var now = DateTime.UtcNow;
            var item = new WishlistItem
            {
                OwnerUserId = ownerId,
                WishlistSourceId = source.Id,
                YouTubeVideoId = entry.VideoId,
                SourceUrl = watchUrl,
                Title = video.Title,
                Artist = video.Artist,
                Album = video.Album,
                DurationMs = video.DurationMs,
                AlbumArt = video.CoverUrl,
                DownloadMusicVideo = true,
                // No save date: YouTube's flat listing does not say when a video was added, and this
                // column is read as "when you saved it on Spotify".
                SpotifyAddedAtUtc = null,
                Status = WishlistItemStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.WishlistItems.Add(item);

            // Per item, like the Deezer path: probing makes a first snapshot slow, so rows appear as they
            // are read and a cancellation keeps what was done.
            try
            {
                await db.SaveChangesAsync(ct);
                added++;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                // The unique (owner, video) index: the other run stored the video between the re-check
                // above and this save. Forget this copy so the context can keep saving, and go on.
                db.Entry(item).State = EntityState.Detached;
                alreadyPresent++;
                logger.LogInformation("YouTube video {VideoId} was already on the wishlist; skipped", entry.VideoId);
            }
        }

        await RecordYouTubeSourceTracksAsync(ownerId, source, playlist.Entries.Select(e => e.VideoId).ToList(), ct);

        // The listing names the playlist, so a rename on YouTube (or a source created while YouTube
        // could not be read, and named after its id) catches up here.
        source.Name = playlist.Title;
        source.ImageUrl = playlist.ThumbnailUrl ?? source.ImageUrl;
        source.LastSyncedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (added > 0)
            logger.LogInformation("Wishlist source {SourceId} ({Name}): added {Added} new YouTube videos", source.Id, LogSanitizer.ForLog(source.Name), added);

        return new WishlistSyncResult(source.Id, added, alreadyPresent);
    }

    private async Task RecordDeezerSourceTracksAsync(
        Guid ownerId, WishlistSource source, IReadOnlyList<(string DeezerId, string? SpotifyId)> remoteOrder, CancellationToken ct)
    {
        var deezerIds = remoteOrder.Select(t => t.DeezerId).Distinct().ToList();
        var spotifyIds = remoteOrder.Where(t => t.SpotifyId != null).Select(t => t.SpotifyId!).Distinct().ToList();
        var items = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId
                && ((w.DeezerTrackId != null && deezerIds.Contains(w.DeezerTrackId))
                    || (w.SpotifyTrackId != null && spotifyIds.Contains(w.SpotifyTrackId))))
            .Select(w => new { w.Id, w.DeezerTrackId, w.SpotifyTrackId })
            .ToListAsync(ct);
        var byDeezer = items.Where(i => i.DeezerTrackId != null)
            .GroupBy(i => i.DeezerTrackId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(i => i.Id), StringComparer.Ordinal);
        var bySpotify = items.Where(i => i.SpotifyTrackId != null)
            .GroupBy(i => i.SpotifyTrackId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(i => i.Id), StringComparer.Ordinal);

        var ordered = new List<int>(remoteOrder.Count);
        foreach (var (deezerId, spotifyId) in remoteOrder)
        {
            if (byDeezer.TryGetValue(deezerId, out var id)
                || (spotifyId is not null && bySpotify.TryGetValue(spotifyId, out id)))
                ordered.Add(id);
        }
        await RecordSourceTracksAsync(source, ordered, ct);
    }

    private async Task RecordYouTubeSourceTracksAsync(
        Guid ownerId, WishlistSource source, IReadOnlyList<string> videoIds, CancellationToken ct)
    {
        var itemIdByVideo = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.YouTubeVideoId != null && videoIds.Contains(w.YouTubeVideoId))
            .Select(w => new { w.Id, w.YouTubeVideoId })
            .ToDictionaryAsync(w => w.YouTubeVideoId!, w => w.Id, StringComparer.Ordinal, ct);

        // A video pasted by hand before YouTubeVideoId existed carries its id only inside its SourceUrl.
        if (videoIds.Any(v => !itemIdByVideo.ContainsKey(v)))
        {
            var legacy = await db.WishlistItems
                .IgnoreQueryFilters()
                .Where(w => w.OwnerUserId == ownerId && w.YouTubeVideoId == null && w.SourceUrl != null)
                .Select(w => new { w.Id, w.SourceUrl })
                .ToListAsync(ct);
            foreach (var row in legacy)
            {
                if (ImportUrlParser.TryParse(row.SourceUrl, out var kind, out var videoId)
                    && kind == ImportUrlKind.YouTube)
                    itemIdByVideo.TryAdd(videoId, row.Id);
            }
        }

        await RecordSourceTracksAsync(source, OrderedItemIds(videoIds, itemIdByVideo), ct);
    }

    private static List<int> OrderedItemIds(IReadOnlyList<string> remoteOrder, IReadOnlyDictionary<string, int> itemIdByKey)
    {
        var ordered = new List<int>(remoteOrder.Count);
        foreach (var key in remoteOrder)
        {
            if (itemIdByKey.TryGetValue(key, out var id))
                ordered.Add(id);
        }
        return ordered;
    }

    /// <summary>
    /// Records the remote list's tracks, in order, as the source's <see cref="WishlistSourceTrack"/>
    /// rows (a track listed twice keeps its first place). Rows are reused by position, so an unchanged
    /// list writes nothing and a track appended at the end is one insert.
    /// </summary>
    private async Task RecordSourceTracksAsync(WishlistSource source, IReadOnlyList<int> itemIds, CancellationToken ct)
    {
        var ordered = itemIds.Distinct().ToList();
        var rows = await db.WishlistSourceTracks
            .IgnoreQueryFilters()
            .Where(t => t.WishlistSourceId == source.Id)
            .OrderBy(t => t.Position)
            .ToListAsync(ct);

        for (var i = 0; i < ordered.Count; i++)
        {
            if (i < rows.Count)
            {
                rows[i].Position = i;
                rows[i].WishlistItemId = ordered[i];
            }
            else
            {
                db.WishlistSourceTracks.Add(new WishlistSourceTrack
                {
                    WishlistSourceId = source.Id,
                    Position = i,
                    WishlistItemId = ordered[i],
                });
            }
        }
        if (rows.Count > ordered.Count)
            db.WishlistSourceTracks.RemoveRange(rows.Skip(ordered.Count));

        source.TracksRecordedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private sealed record YouTubeVideoIdentity(string Title, string Artist, string? Album, int DurationMs, string? CoverUrl);

    /// <summary>
    /// What a new playlist video is, for the wishlist row and the tags the downloader stamps. The probe
    /// reads the video's own page (YouTube Music's artist/track fields, else the channel); when it fails
    /// the video title alone is split as "Artist - Title". Upload noise such as "(Official Music Video)"
    /// is stripped from the title either way: no one edits these rows before they download, and that
    /// noise in TITLE is what sends a downloaded track to review instead of a match.
    /// </summary>
    private async Task<YouTubeVideoIdentity> DescribeVideoAsync(
        YouTubePlaylistEntry entry, string watchUrl, CancellationToken ct)
    {
        var probe = await youTubeVideos.ProbeAsync(watchUrl, ct);
        if (probe.Result is { } r)
            return new YouTubeVideoIdentity(
                CleanTitle(r.Title), r.Artist, r.Album,
                r.DurationMs > 0 ? r.DurationMs : entry.DurationMs,
                r.ThumbnailUrl ?? DefaultThumbnail(entry.VideoId));

        var (artist, title) = YouTubeMetadataResolver.Derive(entry.Title, track: null, artist: null, uploader: null);
        return new YouTubeVideoIdentity(
            CleanTitle(title), artist, Album: null, entry.DurationMs, DefaultThumbnail(entry.VideoId));
    }

    private static string CleanTitle(string title)
    {
        var cleaned = MusicVideoDownloader.StripNoiseSegments(title);
        return cleaned.Length > 0 ? cleaned : title.Trim();
    }

    /// <summary>The one thumbnail every YouTube video has (480×360); the probe usually finds a larger one.</summary>
    private static string DefaultThumbnail(string videoId) => $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

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

        if (type == WishlistSourceType.YouTubePlaylist)
        {
            // First page only: the name, cover and count, not every video.
            var outcome = string.IsNullOrWhiteSpace(playlistId)
                ? null
                : await youTubePlaylists.ReadAsync(playlistId, maxEntries: 1, ct);
            return outcome?.Playlist is { } youTube
                ? (youTube.Title, youTube.ThumbnailUrl)
                : (playlistId ?? "Playlist", null);
        }

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
