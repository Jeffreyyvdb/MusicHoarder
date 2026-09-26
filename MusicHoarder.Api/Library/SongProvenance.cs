using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// The one answer to "why is this track in my library", folding the two provenance facts together:
/// the stored <see cref="SongMetadata.AcquisitionIntent"/> (did album completion add it?) and the
/// derived <see cref="SongOrigin"/> (which root the file sits in, which collection asked for it).
/// </summary>
public enum ProvenanceReason
{
    /// <summary>Already in the source library — a scan found it; nothing was downloaded.</summary>
    LocalFile,
    SpotifyLiked,
    SpotifyPlaylist,
    DeezerPlaylist,
    YouTubePlaylist,

    /// <summary>A one-off "add from URL".</summary>
    Link,
    Synced,

    /// <summary>Downloaded, but the wishlist row that asked for it is gone, so the "why" is lost.</summary>
    Downloaded,

    /// <summary>Album completion queued it because another track of the album was already owned.</summary>
    AlbumFill,
}

/// <summary>One track under a reason, with the moment that reason happened (liked, found, arrived).</summary>
public sealed record ProvenanceTrack(int SongId, string Title, DateTime? AtUtc, string AtLabel);

/// <summary>
/// A track album completion counted as already owned when it filled the album in — what started it.
/// <paramref name="Label"/> is that track's own reason ("Liked on Spotify"), so the chain reads through
/// to the thing the owner actually did.
/// </summary>
public sealed record ProvenanceSeed(
    int SongId,
    string Title,
    string? Album,
    string Reason,
    string Label,
    DateTime? AtUtc,
    string AtLabel,
    bool IsDeleted);

/// <summary>What album completion was completing, when it queued the tracks, and what it started from.</summary>
public sealed record ProvenanceFill(
    string? Album,
    string? Artist,
    DateTime? QueuedAtUtc,
    IReadOnlyList<ProvenanceSeed> Seeds,
    int SeedCount);

public sealed record ProvenanceGroup(
    string Reason,
    string Label,
    string Explanation,
    IReadOnlyList<ProvenanceTrack> Tracks,
    ProvenanceFill? Fill);

/// <summary>
/// <paramref name="Summary"/> is one line for an album header ("Liked on Spotify · 16 filled in");
/// <paramref name="PrimaryReason"/> picks its icon. Both are null when none of the ids were yours.
/// </summary>
public sealed record SongProvenanceResponse(
    string? Summary,
    string? PrimaryReason,
    IReadOnlyList<ProvenanceGroup> Groups);

/// <summary>
/// Explains how a set of songs got into the library — an album page's tracks, or one track's Info.
/// <para>
/// Derived, like <see cref="SongOriginResolver"/>, and for the same reason: it stays correct for rows
/// that predate it. The one fact nothing stores is which owned track started an album fill, and that
/// is recoverable too: <c>AlbumCompletionSweep</c> fills a <see cref="CanonicalAlbum"/> because the
/// owner held songs grouped under its (artist, album) key, so the explicit songs under that key are
/// the ones it started from. Soft-deleted songs stay in the answer, flagged, because a seed deleted
/// after the fill is still the reason the fill happened.
/// </para>
/// <para>
/// The text is composed here rather than in each client for the reason the History feed does it:
/// two clients render it, and the explanation must not drift between them.
/// </para>
/// </summary>
public static class SongProvenanceService
{
    /// <summary>How many seeds a fill names before it just counts them.</summary>
    public const int MaxSeeds = 5;

    private sealed record SongFacts(
        int Id,
        string? Title,
        string FileName,
        string? Artist,
        string? AlbumArtist,
        string? Album,
        string SourcePath,
        string? SpotifyId,
        SongAcquisitionIntent Intent,
        DateTime? AcquiredAtUtc,
        DateTime IndexedAtUtc,
        DateTime? DeletedAtUtc);

    private sealed record Classified(SongFacts Song, ProvenanceReason Reason, string? Detail, DateTime? AtUtc, string AtLabel);

    public static readonly SongProvenanceResponse Empty = new(null, null, []);

    /// <summary>
    /// Every read goes through the ambient owner filter, so ids the caller does not own (a grantor's
    /// songs, another tenant's) simply drop out — provenance is the owner's pipeline history and is
    /// never published through a grant.
    /// </summary>
    public static async Task<SongProvenanceResponse> BuildAsync(
        MusicHoarderDbContext db,
        IReadOnlyCollection<int> songIds,
        string? downloadDirectory,
        string? syncedSourceDirectory,
        CancellationToken ct)
    {
        if (songIds.Count == 0) return Empty;

        var songs = await SelectFacts(db.Songs.AsNoTracking().Where(s => songIds.Contains(s.Id)))
            .ToListAsync(ct);
        if (songs.Count == 0) return Empty;

        var saveDates = await SpotifySaveDates.LoadAsync(db, ct);
        var classified = songs
            .Select(s => Classify(s, saveDates, downloadDirectory, syncedSourceDirectory))
            .ToList();

        var groups = new List<ProvenanceGroup>();

        // Everything you asked for, one group per collection, the biggest first.
        foreach (var group in classified
                     .Where(c => c.Reason != ProvenanceReason.AlbumFill)
                     .GroupBy(c => (c.Reason, c.Detail))
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key.Reason))
        {
            var (label, explanation) = Describe(group.Key.Reason, group.Key.Detail);
            groups.Add(new ProvenanceGroup(
                group.Key.Reason.ToString(), label, explanation, Tracks(group), Fill: null));
        }

        var fills = classified.Where(c => c.Reason == ProvenanceReason.AlbumFill).ToList();
        if (fills.Count > 0)
            groups.AddRange(await DescribeFillsAsync(db, fills, saveDates, downloadDirectory, syncedSourceDirectory, ct));

        return new SongProvenanceResponse(Summarize(groups), groups.FirstOrDefault()?.Reason, groups);
    }

    /// <summary>
    /// The reason for one song. Intent wins: an album-fill download is filed under album completion
    /// even when its wishlist link has since gone. A scanned file stays a local file even when a Spotify
    /// like also points at it — the like found it already here (<c>SkippedOwned</c>), it did not bring it.
    /// </summary>
    public static ProvenanceReason ReasonFor(SongAcquisitionIntent intent, SongOrigin origin)
    {
        if (intent == SongAcquisitionIntent.AlbumFill) return ProvenanceReason.AlbumFill;
        return origin.Kind switch
        {
            SongOriginKind.Synced => ProvenanceReason.Synced,
            SongOriginKind.Scanned => ProvenanceReason.LocalFile,
            _ => origin.Source switch
            {
                SongOriginSource.SpotifyLiked => ProvenanceReason.SpotifyLiked,
                SongOriginSource.SpotifyPlaylist => ProvenanceReason.SpotifyPlaylist,
                SongOriginSource.DeezerPlaylist => ProvenanceReason.DeezerPlaylist,
                SongOriginSource.YouTubePlaylist => ProvenanceReason.YouTubePlaylist,
                SongOriginSource.DirectUrl => ProvenanceReason.Link,
                SongOriginSource.AlbumCompletion => ProvenanceReason.AlbumFill,
                _ => ProvenanceReason.Downloaded,
            },
        };
    }

    /// <summary>A reason's label (a group header, and a seed's tag) and its one-sentence explanation.</summary>
    public static (string Label, string Explanation) Describe(ProvenanceReason reason, string? detail) => reason switch
    {
        ProvenanceReason.LocalFile => (
            "From your music folder",
            "Already in your music folder when a scan found it — nothing was downloaded."),
        ProvenanceReason.SpotifyLiked => (
            "Liked on Spotify",
            "In your Spotify Liked Songs, so the wishlist downloaded it."),
        ProvenanceReason.SpotifyPlaylist => (
            detail is null ? "From a Spotify playlist" : $"From Spotify playlist {Quote(detail)}",
            "On a Spotify playlist you sync to the wishlist, so the wishlist downloaded it."),
        ProvenanceReason.DeezerPlaylist => (
            detail is null ? "From a Deezer playlist" : $"From Deezer playlist {Quote(detail)}",
            "On a Deezer playlist you sync to the wishlist, so the wishlist downloaded it."),
        ProvenanceReason.YouTubePlaylist => (
            detail is null ? "From a YouTube playlist" : $"From YouTube playlist {Quote(detail)}",
            "On a YouTube playlist you sync to the wishlist, so the wishlist downloaded it from that video."),
        ProvenanceReason.Link => (
            detail is null ? "Added from a link" : $"Added from {detail}",
            "Downloaded from a link you added by hand."),
        ProvenanceReason.Synced => (
            "Synced from another instance",
            "Sent here by another MusicHoarder instance’s sync."),
        ProvenanceReason.Downloaded => (
            "Downloaded by MusicHoarder",
            "Downloaded by MusicHoarder, but the wishlist entry that asked for it is gone, so the reason was not kept."),
        _ => (
            "Filled in by album completion",
            "Album completion added it because you already had another track from the same album."),
    };

    private static IQueryable<SongFacts> SelectFacts(IQueryable<SongMetadata> query) =>
        query.Select(s => new SongFacts(
            s.Id, s.Title, s.FileName, s.Artist, s.AlbumArtist, s.Album, s.SourcePath, s.SpotifyId,
            s.AcquisitionIntent, s.AcquiredAtUtc, s.IndexedAtUtc, s.DeletedAtUtc));

    private static Classified Classify(
        SongFacts song, SpotifySaveDates saveDates, string? downloadDirectory, string? syncedSourceDirectory)
    {
        var origin = SongOriginResolver.Resolve(song.SourcePath, saveDates.LinkFor(song.Id), downloadDirectory, syncedSourceDirectory);
        var reason = ReasonFor(song.Intent, origin);
        var arrived = song.AcquiredAtUtc ?? song.IndexedAtUtc;

        // The date that belongs to the reason: Spotify's own for a like or a playlist add (the moment
        // the owner acted), the arrival here for everything MusicHoarder did on its own.
        var (at, atLabel) = reason switch
        {
            ProvenanceReason.SpotifyLiked when saveDates.SaveDateFor(song.Id, song.SpotifyId, origin.SpotifyAddedAtUtc) is { } liked
                => (liked, "Liked"),
            ProvenanceReason.SpotifyPlaylist when origin.SpotifyAddedAtUtc is { } added
                => (added, "Added to the playlist"),
            ProvenanceReason.LocalFile => (arrived, "Found"),
            ProvenanceReason.Synced => (arrived, "Synced"),
            _ => (arrived, "Arrived"),
        };

        var detail = reason is ProvenanceReason.SpotifyPlaylist or ProvenanceReason.DeezerPlaylist
                or ProvenanceReason.YouTubePlaylist or ProvenanceReason.Link
            ? origin.Detail
            : null;
        return new Classified(song, reason, detail, at, atLabel);
    }

    private static List<ProvenanceTrack> Tracks(IEnumerable<Classified> rows) =>
        rows.Select(c => new ProvenanceTrack(c.Song.Id, TitleOf(c.Song), c.AtUtc, c.AtLabel)).ToList();

    /// <summary>
    /// Album-fill songs, one group per album completion was completing. Each names the owned songs it
    /// started from — the explicit songs under that canonical album's (artist, album) key.
    /// </summary>
    private static async Task<List<ProvenanceGroup>> DescribeFillsAsync(
        MusicHoarderDbContext db,
        List<Classified> fills,
        SpotifySaveDates saveDates,
        string? downloadDirectory,
        string? syncedSourceDirectory,
        CancellationToken ct)
    {
        var fillIds = fills.Select(f => f.Song.Id).ToList();
        var items = await db.WishlistItems
            .AsNoTracking()
            .Where(w => w.Origin == WishlistItemOrigin.AlbumCompletion
                && w.CanonicalAlbumId != null
                && w.DownloadedSongId != null
                && fillIds.Contains(w.DownloadedSongId!.Value))
            .Select(w => new { SongId = w.DownloadedSongId!.Value, AlbumId = w.CanonicalAlbumId!.Value, w.CreatedAtUtc })
            .ToListAsync(ct);
        // A song re-queued by a later edition has two items; the first one is what brought it in.
        var itemBySong = items
            .GroupBy(i => i.SongId)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.CreatedAtUtc).First());

        var albumIds = itemBySong.Values.Select(i => i.AlbumId).Distinct().ToList();
        var albums = await db.CanonicalAlbums
            .AsNoTracking()
            .Where(a => albumIds.Contains(a.Id))
            .Select(a => new { a.Id, a.ArtistKey, a.AlbumKey, a.DisplayTitle, a.DisplayArtist })
            .ToListAsync(ct);
        var albumById = albums.ToDictionary(a => a.Id);

        // The sweep's own grouping, recomputed: explicit, non-duplicate songs with an album, keyed the
        // way AlbumCompletionSweep keys them (normalized album artist, falling back to the track artist).
        var seedsByKey = new Dictionary<(string, string), List<Classified>>();
        if (albums.Count > 0)
        {
            var wanted = albums.Select(a => (a.ArtistKey, a.AlbumKey)).ToHashSet();
            var candidates = await SelectFacts(db.Songs
                    .AsNoTracking()
                    .Where(s => s.AcquisitionIntent == SongAcquisitionIntent.Explicit
                        && !s.IsDuplicate
                        && s.Album != null && s.Album != ""))
                .ToListAsync(ct);
            foreach (var song in candidates)
            {
                var key = (
                    TitleNormalizer.NormalizeForSearch(song.AlbumArtist ?? song.Artist),
                    TitleNormalizer.NormalizeForSearch(song.Album));
                if (!wanted.Contains(key)) continue;
                if (!seedsByKey.TryGetValue(key, out var list)) seedsByKey[key] = list = [];
                list.Add(Classify(song, saveDates, downloadDirectory, syncedSourceDirectory));
            }
        }

        var groups = new List<ProvenanceGroup>();
        foreach (var group in fills
                     .GroupBy(f => itemBySong.TryGetValue(f.Song.Id, out var item) && albumById.ContainsKey(item.AlbumId)
                         ? item.AlbumId
                         : (int?)null)
                     .OrderByDescending(g => g.Count()))
        {
            if (group.Key is not { } albumId)
            {
                var (label, explanation) = Describe(ProvenanceReason.AlbumFill, null);
                groups.Add(new ProvenanceGroup(
                    nameof(ProvenanceReason.AlbumFill), label, explanation, Tracks(group), Fill: null));
                continue;
            }

            var album = albumById[albumId];
            // Live seeds before deleted ones, then the earliest arrival — the likeliest trigger.
            var seeds = seedsByKey.GetValueOrDefault((album.ArtistKey, album.AlbumKey)) ?? [];
            var ordered = seeds
                .OrderBy(s => s.Song.DeletedAtUtc != null)
                .ThenBy(s => s.Song.AcquiredAtUtc ?? s.Song.IndexedAtUtc)
                .ThenBy(s => s.Song.Id)
                .ToList();
            var queuedAt = group.Min(f => itemBySong[f.Song.Id].CreatedAtUtc);

            groups.Add(new ProvenanceGroup(
                nameof(ProvenanceReason.AlbumFill),
                Describe(ProvenanceReason.AlbumFill, null).Label,
                ExplainFill(album.DisplayTitle, ordered),
                Tracks(group),
                new ProvenanceFill(
                    album.DisplayTitle,
                    album.DisplayArtist,
                    queuedAt,
                    ordered.Take(MaxSeeds).Select(ToSeed).ToList(),
                    ordered.Count)));
        }

        return groups;
    }

    private static ProvenanceSeed ToSeed(Classified c) => new(
        c.Song.Id,
        TitleOf(c.Song),
        c.Song.Album,
        c.Reason.ToString(),
        Describe(c.Reason, c.Detail).Label,
        c.AtUtc,
        c.AtLabel,
        IsDeleted: c.Song.DeletedAtUtc != null);

    private static string ExplainFill(string? album, IReadOnlyList<Classified> seeds)
    {
        var target = string.IsNullOrWhiteSpace(album) ? "the album" : Quote(album);
        if (seeds.Count == 0)
            return $"Album completion queued the missing tracks of {target}, but the track that started it is no longer in your library.";

        var had = seeds.Count switch
        {
            1 => Quote(TitleOf(seeds[0].Song)),
            2 => $"{Quote(TitleOf(seeds[0].Song))} and {Quote(TitleOf(seeds[1].Song))}",
            _ => $"{Quote(TitleOf(seeds[0].Song))} and {seeds.Count - 1} other tracks",
        };
        return $"You had {had} from {target}, so album completion looked up its full tracklist and queued the tracks you were missing.";
    }

    /// <summary>
    /// "Liked on Spotify · 16 filled in": the biggest collection you asked for, how many other
    /// collections there were, and how much album completion added on top.
    /// </summary>
    private static string? Summarize(List<ProvenanceGroup> groups)
    {
        if (groups.Count == 0) return null;
        var asked = groups.Where(g => g.Reason != nameof(ProvenanceReason.AlbumFill)).ToList();
        var filled = groups.Where(g => g.Reason == nameof(ProvenanceReason.AlbumFill)).Sum(g => g.Tracks.Count);
        if (asked.Count == 0) return Describe(ProvenanceReason.AlbumFill, null).Label;

        var head = asked.Count == 1 ? asked[0].Label : $"{asked[0].Label} + {asked.Count - 1} more";
        return filled > 0 ? $"{head} · {filled} filled in" : head;
    }

    private static string TitleOf(SongFacts s) =>
        string.IsNullOrWhiteSpace(s.Title) ? Path.GetFileNameWithoutExtension(s.FileName) : s.Title.Trim();

    private static string Quote(string value) => $"“{value}”";
}
