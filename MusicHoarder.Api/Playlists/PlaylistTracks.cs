using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Playlists;

/// <summary>One track of a playlist before it is checked against a library: the song it points at, if any yet.</summary>
/// <param name="SongId">The song, or null for a synced track that is not in the library yet.</param>
/// <param name="FromSource">True for a track of the remote playlist, false for one added in MusicHoarder.</param>
public readonly record struct PlaylistTrackRef(int? SongId, bool FromSource);

/// <summary>What a playlist plays, in order, once its tracks are checked against a library.</summary>
/// <param name="SongIds">Every playable song: the synced tracks, then the MusicHoarder additions.</param>
/// <param name="AddedSongIds">The subset added in MusicHoarder (the only ones that can be removed here).</param>
/// <param name="MissingCount">Synced tracks that are not in the library yet.</param>
public sealed record ResolvedPlaylistTracks(IReadOnlyList<int> SongIds, IReadOnlyList<int> AddedSongIds, int MissingCount);

/// <summary>
/// Reads what a playlist contains. Synced tracks come from the source's <see cref="WishlistSourceTrack"/>
/// rows: each points at a wishlist item, whose song is the one it downloaded or found already owned —
/// or, while the download worker has not reached it, the Spotify match cache's in-library match, the
/// same fact the worker would act on. A source whose tracklist was never recorded falls back to the
/// items it introduced itself (a subset: items are deduplicated across sources).
/// <para>
/// A song id is then mapped to what should play: a soft-deleted song drops out, and a duplicate plays
/// its keeper, so a playlist survives the duplicate queue.
/// </para>
/// </summary>
public static class PlaylistTracks
{
    /// <summary>
    /// The unchecked tracks of each playlist, keyed by playlist id. <paramref name="playlists"/> must
    /// carry their <see cref="Playlist.Entries"/> and be rows the caller may read; the reads below go
    /// by the ids they name, so they bypass the ambient filter (the exporter has no signed-in user).
    /// </summary>
    public static async Task<Dictionary<int, List<PlaylistTrackRef>>> LoadRefsAsync(
        MusicHoarderDbContext db,
        IReadOnlyCollection<Playlist> playlists,
        IReadOnlyDictionary<int, WishlistSource> sources,
        CancellationToken ct)
    {
        var bySource = await LoadSourceSongIdsAsync(db, sources.Values, ct);

        var result = new Dictionary<int, List<PlaylistTrackRef>>(playlists.Count);
        foreach (var playlist in playlists)
        {
            var refs = new List<PlaylistTrackRef>();
            if (playlist.WishlistSourceId is { } sourceId && bySource.TryGetValue(sourceId, out var synced))
                refs.AddRange(synced.Select(id => new PlaylistTrackRef(id, FromSource: true)));
            refs.AddRange(playlist.Entries
                .OrderBy(e => e.Position)
                .ThenBy(e => e.Id)
                .Select(e => new PlaylistTrackRef(e.SongId, FromSource: false)));
            result[playlist.Id] = refs;
        }
        return result;
    }

    /// <summary>
    /// Applies a song-id mapping (see <see cref="PlayableForCallerAsync"/>) to a playlist's tracks. A
    /// song plays once, at its first place; an addition that is also a synced track is not "added".
    /// </summary>
    public static ResolvedPlaylistTracks Resolve(IReadOnlyList<PlaylistTrackRef> refs, IReadOnlyDictionary<int, int> playable)
    {
        var songIds = new List<int>(refs.Count);
        var added = new List<int>();
        var seen = new HashSet<int>();
        var missing = 0;
        foreach (var track in refs)
        {
            if (track.SongId is not { } raw)
            {
                if (track.FromSource) missing++;
                continue;
            }
            if (!playable.TryGetValue(raw, out var id) || !seen.Add(id))
                continue;
            songIds.Add(id);
            if (!track.FromSource) added.Add(id);
        }
        return new ResolvedPlaylistTracks(songIds, added, missing);
    }

    /// <summary>
    /// Maps each candidate song id to the id the caller should play, leaving out what they may not
    /// read. The caller's own songs go through the ambient filter (a soft-deleted song drops out, a
    /// duplicate becomes its keeper); a grantor's songs only through the library scope, which already
    /// leaves out deleted and duplicate rows — so a revoked grant empties them from the playlist.
    /// </summary>
    public static async Task<Dictionary<int, int>> PlayableForCallerAsync(
        MusicHoarderDbContext db, ILibraryScope scope, IReadOnlyCollection<int> candidateIds, CancellationToken ct)
    {
        var result = new Dictionary<int, int>();
        if (candidateIds.Count == 0) return result;
        var ids = candidateIds.Distinct().ToList();

        var self = scope.Slices.First(s => s.IsSelf);
        await MapOwnAsync(scope.SongsFor(db, self), ids, result, ct);

        foreach (var slice in scope.GrantedSlices)
        {
            var remaining = ids.Where(id => !result.ContainsKey(id)).ToList();
            if (remaining.Count == 0) break;
            var visible = await scope.SongsFor(db, slice)
                .Where(s => remaining.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct);
            foreach (var id in visible) result[id] = id;
        }
        return result;
    }

    /// <summary>
    /// The owner's songs that are built into the destination library, keyed by candidate id — what an
    /// exported <c>.m3u8</c> can point at. Runs outside any request, so it scopes to
    /// <paramref name="ownerId"/> explicitly.
    /// </summary>
    public static async Task<Dictionary<int, BuiltSong>> BuiltForOwnerAsync(
        MusicHoarderDbContext db, Guid ownerId, IReadOnlyCollection<int> candidateIds, CancellationToken ct)
    {
        var playable = await PlayableForOwnerAsync(db, ownerId, candidateIds, ct);
        var ownSongs = db.Songs.IgnoreQueryFilters().Where(s => s.OwnerUserId == ownerId);

        var targetIds = playable.Values.Distinct().ToList();
        var built = await ownSongs
            .AsNoTracking()
            .Where(s => targetIds.Contains(s.Id)
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.DestinationPath != null)
            .Select(s => new BuiltSong(s.Id, s.DestinationPath!, s.Artist, s.Title, s.DurationSeconds))
            .ToDictionaryAsync(s => s.Id, ct);

        var result = new Dictionary<int, BuiltSong>();
        foreach (var (candidate, target) in playable)
        {
            if (built.TryGetValue(target, out var song))
                result[candidate] = song;
        }
        return result;
    }

    /// <summary>
    /// <see cref="PlayableForCallerAsync"/> for one owner's own songs, outside any request (scoped to
    /// <paramref name="ownerId"/> explicitly).
    /// </summary>
    public static async Task<Dictionary<int, int>> PlayableForOwnerAsync(
        MusicHoarderDbContext db, Guid ownerId, IReadOnlyCollection<int> candidateIds, CancellationToken ct)
    {
        var result = new Dictionary<int, int>();
        if (candidateIds.Count == 0) return result;
        var ownSongs = db.Songs.IgnoreQueryFilters().Where(s => s.OwnerUserId == ownerId);
        await MapOwnAsync(ownSongs, candidateIds.Distinct().ToList(), result, ct);
        return result;
    }

    /// <summary>A built song, as an exported playlist line needs it.</summary>
    public sealed record BuiltSong(int Id, string DestinationPath, string? Artist, string? Title, int? DurationSeconds);

    /// <summary>
    /// Own songs: live ones map to themselves, a duplicate to its live keeper, a deleted song to nothing.
    /// </summary>
    private static async Task MapOwnAsync(
        IQueryable<SongMetadata> songs, IReadOnlyList<int> ids, Dictionary<int, int> result, CancellationToken ct)
    {
        var rows = await songs.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.DeletedAtUtc == null)
            .Select(s => new { s.Id, s.IsDuplicate, s.DuplicateOfId })
            .ToListAsync(ct);

        var keeperIds = rows
            .Where(r => r.IsDuplicate && r.DuplicateOfId != null)
            .Select(r => r.DuplicateOfId!.Value)
            .Distinct()
            .ToList();
        var liveKeepers = keeperIds.Count == 0
            ? []
            : (await songs.AsNoTracking()
                .Where(s => keeperIds.Contains(s.Id) && s.DeletedAtUtc == null && !s.IsDuplicate)
                .Select(s => s.Id)
                .ToListAsync(ct)).ToHashSet();

        foreach (var row in rows)
        {
            if (!row.IsDuplicate)
                result[row.Id] = row.Id;
            else if (row.DuplicateOfId is { } keeper && liveKeepers.Contains(keeper))
                result[row.Id] = keeper;
            else
                result[row.Id] = row.Id;
        }
    }

    /// <summary>Each source's tracks in order, as song ids (null while a track is not in the library).</summary>
    private static async Task<Dictionary<int, List<int?>>> LoadSourceSongIdsAsync(
        MusicHoarderDbContext db, IEnumerable<WishlistSource> sources, CancellationToken ct)
    {
        var result = new Dictionary<int, List<int?>>();
        var list = sources.ToList();
        if (list.Count == 0) return result;

        var recordedIds = list.Where(s => s.TracksRecordedAtUtc != null).Select(s => s.Id).ToList();
        var fallbackIds = list.Where(s => s.TracksRecordedAtUtc == null).Select(s => s.Id).ToList();

        var tracks = new List<SourceItem>();
        if (recordedIds.Count > 0)
        {
            tracks.AddRange(await db.WishlistSourceTracks
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => recordedIds.Contains(t.WishlistSourceId))
                .OrderBy(t => t.WishlistSourceId).ThenBy(t => t.Position)
                .Select(t => new SourceItem(
                    t.WishlistSourceId,
                    t.WishlistItem.OwnerUserId,
                    t.WishlistItem.DownloadedSongId,
                    t.WishlistItem.SpotifyTrackId,
                    null,
                    t.WishlistItem.Id))
                .ToListAsync(ct));
        }
        if (fallbackIds.Count > 0)
        {
            var introduced = await db.WishlistItems
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.WishlistSourceId != null && fallbackIds.Contains(w.WishlistSourceId!.Value))
                .Select(w => new SourceItem(
                    w.WishlistSourceId!.Value,
                    w.OwnerUserId,
                    w.DownloadedSongId,
                    w.SpotifyTrackId,
                    w.SpotifyAddedAtUtc,
                    w.Id))
                .ToListAsync(ct);
            var likedIds = list.Where(s => s.SourceType == WishlistSourceType.LikedSongs).Select(s => s.Id).ToHashSet();
            foreach (var group in introduced.GroupBy(t => t.SourceId))
            {
                // Liked Songs reads newest first, like Spotify; a playlist in the order it was added.
                tracks.AddRange(likedIds.Contains(group.Key)
                    ? group.OrderByDescending(t => t.AddedAtUtc ?? DateTime.MinValue).ThenBy(t => t.ItemId)
                    : group.OrderBy(t => t.AddedAtUtc ?? DateTime.MaxValue).ThenBy(t => t.ItemId));
            }
        }

        var matches = await LoadInLibraryMatchesAsync(db, tracks, ct);
        foreach (var track in tracks)
        {
            if (!result.TryGetValue(track.SourceId, out var ids))
                result[track.SourceId] = ids = [];
            var songId = track.DownloadedSongId;
            if (songId is null && track.SpotifyTrackId is { } spotifyId)
                songId = matches.GetValueOrDefault((track.OwnerUserId, spotifyId));
            ids.Add(songId);
        }
        return result;
    }

    private sealed record SourceItem(
        int SourceId, Guid OwnerUserId, int? DownloadedSongId, string? SpotifyTrackId, DateTime? AddedAtUtc, int ItemId);

    /// <summary>The Spotify match cache's confident in-library songs, for items the worker has not linked yet.</summary>
    private static async Task<Dictionary<(Guid, string), int?>> LoadInLibraryMatchesAsync(
        MusicHoarderDbContext db, IReadOnlyList<SourceItem> tracks, CancellationToken ct)
    {
        var pending = tracks
            .Where(t => t.DownloadedSongId is null && t.SpotifyTrackId is not null)
            .ToList();
        if (pending.Count == 0) return [];

        var owners = pending.Select(t => t.OwnerUserId).Distinct().ToList();
        var spotifyIds = pending.Select(t => t.SpotifyTrackId!).Distinct().ToList();
        var inLibrary = (int)ComparisonMatchStatus.InLibrary;
        var rows = await db.SpotifyTrackLibraryMatches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => owners.Contains(m.OwnerUserId)
                && spotifyIds.Contains(m.SpotifyTrackId)
                && m.MatchStatus == inLibrary
                && m.MatchedSongId != null)
            .Select(m => new { m.OwnerUserId, m.SpotifyTrackId, m.MatchedSongId })
            .ToListAsync(ct);
        return rows.ToDictionary(r => (r.OwnerUserId, r.SpotifyTrackId), r => r.MatchedSongId);
    }
}
