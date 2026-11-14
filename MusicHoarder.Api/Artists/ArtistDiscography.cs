using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artists;

/// <summary>
/// One release on an artist's discography, after its editions are folded together: the catalog lists
/// the explicit and the clean cut, the standard and the "(Deluxe)" edition, as separate albums.
/// </summary>
/// <param name="Key">Kind plus normalized title — the identity the monitor's seen set and the UI key on.</param>
/// <param name="PrimaryId">The edition shown and fetched first: the explicit one, then the most qualified title.</param>
/// <param name="EditionIds">Every catalog id of the release, <paramref name="PrimaryId"/> first.</param>
/// <param name="ReleaseDate">The earliest edition's date — when the release first came out.</param>
public sealed record DiscographyRelease(
    string Key,
    ArtistReleaseKinds Kind,
    string Title,
    DateOnly? ReleaseDate,
    string? CoverUrl,
    bool Explicit,
    string PrimaryId,
    IReadOnlyList<string> EditionIds);

/// <summary>A song the discography reads to decide what the owner already has.</summary>
public sealed record ArtistSongFacts(
    int Id,
    string? Title,
    string? Album,
    string? AlbumArtist,
    string? Artist,
    string? Artists,
    string? MusicBrainzId,
    int? DiscNumber,
    int? TrackNumber,
    SongAcquisitionIntent Intent);

/// <summary>
/// The pure rules behind an artist's discography: what kind a release is, which catalog entries are
/// one release, and which of the owner's songs are this artist's. Kept free of I/O so each rule is
/// tested on its own.
/// </summary>
public static class ArtistDiscography
{
    /// <summary>Deezer's <c>record_type</c> to a kind. Anything unknown is treated as an album.</summary>
    public static ArtistReleaseKinds KindOf(string? recordType) => recordType?.Trim().ToLowerInvariant() switch
    {
        "single" => ArtistReleaseKinds.Single,
        "ep" => ArtistReleaseKinds.Ep,
        "compile" or "compilation" => ArtistReleaseKinds.Compilation,
        _ => ArtistReleaseKinds.Album,
    };

    /// <summary>The wire name of a kind, as the web app filters on it.</summary>
    public static string WireKind(ArtistReleaseKinds kind) => kind switch
    {
        ArtistReleaseKinds.Single => "single",
        ArtistReleaseKinds.Ep => "ep",
        ArtistReleaseKinds.Compilation => "compilation",
        _ => "album",
    };

    /// <summary>
    /// Kind plus the search-normalized title. That normalization drops parenthesized and bracketed
    /// qualifiers, so "Album", "Album (Deluxe Edition)" and "Album [Explicit]" are one release — the
    /// same folding <see cref="CanonicalAlbum"/>'s album key uses, which is what lets the two join.
    /// </summary>
    public static string ReleaseKey(ArtistReleaseKinds kind, string title)
    {
        var normalized = TitleNormalizer.NormalizeForSearch(title);
        if (normalized.Length == 0) normalized = title.Trim().ToLowerInvariant();
        return $"{WireKind(kind)}:{normalized}";
    }

    /// <summary>
    /// Folds a catalog listing into releases, newest first. Within a release the edition kept is the
    /// explicit one, then the one with the longest title (the "Deluxe" or "Expanded" edition — the
    /// biggest tracklist, which is what a hoarder wants), then the earliest.
    /// </summary>
    public static IReadOnlyList<DiscographyRelease> Group(IEnumerable<DeezerArtistRelease> releases) =>
        releases
            .GroupBy(r => ReleaseKey(KindOf(r.RecordType), r.Title))
            .Select(g =>
            {
                var editions = g
                    .OrderByDescending(r => r.Explicit)
                    .ThenByDescending(r => r.Title.Length)
                    .ThenBy(r => r.ReleaseDate ?? DateOnly.MaxValue)
                    .ThenBy(r => r.Id, StringComparer.Ordinal)
                    .ToList();
                var primary = editions[0];
                var firstOut = g.Where(r => r.ReleaseDate is not null).Min(r => r.ReleaseDate);
                return new DiscographyRelease(
                    g.Key,
                    KindOf(primary.RecordType),
                    // The plain title reads better on a card; the edition is what gets fetched.
                    editions.OrderBy(r => r.Title.Length).First().Title,
                    firstOut,
                    primary.CoverUrl ?? editions.Select(e => e.CoverUrl).FirstOrDefault(c => c is not null),
                    primary.Explicit,
                    primary.Id,
                    editions.Select(e => e.Id).Distinct(StringComparer.Ordinal).ToList());
            })
            .OrderByDescending(r => r.ReleaseDate ?? DateOnly.MinValue)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether a song is this artist's: its album artist (else artist) is the name, or the name is one
    /// of its discrete credits. The same two ways the artist page reaches a song.
    /// </summary>
    public static bool IsByArtist(ArtistSongFacts song, string artistKey)
    {
        if (artistKey.Length == 0) return false;
        if (TitleNormalizer.NormalizeForSearch(song.AlbumArtist ?? song.Artist) == artistKey) return true;
        return (song.Artists ?? string.Empty)
            .Split(';')
            .Any(n => TitleNormalizer.NormalizeForSearch(n) == artistKey);
    }

    /// <summary>
    /// Whether a catalog artist's listing proves it is the library's artist: one of its releases is an
    /// album (or a single's title) the owner already has from this artist. Album-fill downloads do not
    /// count — they exist because of a catalog, so they cannot vouch for one (the rule
    /// <see cref="CanonicalAlbumMatch"/> learned the hard way).
    /// </summary>
    public static bool ProvesIdentity(IEnumerable<DeezerArtistRelease> releases, IEnumerable<ArtistSongFacts> ownedByArtist)
    {
        var owned = ownedByArtist.Where(s => s.Intent != SongAcquisitionIntent.AlbumFill).ToList();
        var albumKeys = owned
            .Select(s => TitleNormalizer.NormalizeForSearch(s.Album))
            .Where(k => k.Length > 0 && !IsPlaceholderAlbum(k))
            .ToHashSet(StringComparer.Ordinal);
        var titleKeys = owned
            .Select(s => TitleNormalizer.NormalizeForSearch(s.Title))
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var release in releases)
        {
            var key = TitleNormalizer.NormalizeForSearch(release.Title);
            if (key.Length == 0) continue;
            if (albumKeys.Contains(key)) return true;
            // A single is named after its song: owning the song is owning the release.
            if (KindOf(release.RecordType) is ArtistReleaseKinds.Single or ArtistReleaseKinds.Ep && titleKeys.Contains(key))
                return true;
        }

        return false;
    }

    private static bool IsPlaceholderAlbum(string key) =>
        key is "unknown album" or "unknown" or "singles" or "single";
}
