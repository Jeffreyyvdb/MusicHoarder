using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Navidrome;

/// <summary>
/// The MusicHoarder side of a like match, reduced to the keys that identify a song across both
/// systems: the library-relative destination and source paths (each mapping onto a Navidrome
/// library), the MusicBrainz recording id, and a normalized artist+title+duration for the fuzzy
/// fallback. Built from a <see cref="Persistence.SongMetadata"/> row via <see cref="NavidromeLikeMatcher"/>.
/// </summary>
public sealed record LikeMatchKey(
    int SongId,
    string? DestinationRelPath,
    string? SourceRelPath,
    string? MusicBrainzId,
    string? NormalizedArtist,
    string? NormalizedTitle,
    int? DurationSeconds);

/// <summary>
/// Resolves which Navidrome song(s) correspond to a MusicHoarder song, and vice-versa. Match order
/// is deliberate: library-relative <b>path</b> first (exact and container-agnostic — MH's destination
/// dir maps onto Navidrome's <c>musichoarderv3</c> library, its source dir onto <c>/music</c>), then
/// MusicBrainz recording id, then a normalized artist+title match within a duration tolerance. Paths
/// are matched case-insensitively on forward slashes so container mount casing never matters.
/// </summary>
public static class NavidromeLikeMatcher
{
    /// <summary>
    /// Builds the cross-system match key for a song. <paramref name="destinationDirectory"/> /
    /// <paramref name="sourceDirectory"/> are the configured roots the absolute paths are made
    /// relative to (so the result lines up with Navidrome's library-relative paths).
    /// </summary>
    public static LikeMatchKey BuildKey(
        int songId,
        string? sourcePath,
        string? destinationPath,
        string? musicBrainzId,
        string? artist,
        string? title,
        int? durationSeconds,
        string sourceDirectory,
        string destinationDirectory)
    {
        return new LikeMatchKey(
            songId,
            DestinationRelPath: ToRelative(destinationPath, destinationDirectory),
            SourceRelPath: ToRelative(sourcePath, sourceDirectory),
            MusicBrainzId: Blank(musicBrainzId),
            NormalizedArtist: NormalizeText(artist),
            NormalizedTitle: NormalizeText(title),
            DurationSeconds: durationSeconds);
    }

    /// <summary>Normalizes an absolute or already-relative path to the comparison form Navidrome paths use.</summary>
    public static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/').Trim().ToLowerInvariant();

    /// <summary>
    /// Path of <paramref name="absolutePath"/> relative to <paramref name="baseDirectory"/>, normalized
    /// for comparison. Null when either is blank or the file is not under the base directory.
    /// </summary>
    public static string? ToRelative(string? absolutePath, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(baseDirectory))
            return null;

        var rel = Path.GetRelativePath(baseDirectory, absolutePath);
        if (string.IsNullOrEmpty(rel) || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            return null;

        return NormalizePath(rel);
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var norm = TitleNormalizer.NormalizeForSearch(value);
        return string.IsNullOrWhiteSpace(norm) ? null : norm;
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    internal static string? NavPathKey(NavidromeSong s)
        => string.IsNullOrWhiteSpace(s.Path) ? null : NormalizePath(s.Path);
}

/// <summary>
/// An in-memory index over a set of Navidrome songs (a starred snapshot, or one search's results),
/// queryable by a MusicHoarder <see cref="LikeMatchKey"/>. Returns matches in the strongest available
/// tier only (path, else MBID, else fuzzy) so a solid path hit is never diluted by fuzzy noise.
/// </summary>
public sealed class NavidromeSongIndex
{
    private readonly Dictionary<string, List<NavidromeSong>> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NavidromeSong>> _byMbid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<NavidromeSong>> _byArtistTitle = new(StringComparer.Ordinal);
    private readonly int _fuzzyDurationToleranceSeconds;

    public NavidromeSongIndex(IEnumerable<NavidromeSong> songs, int fuzzyDurationToleranceSeconds)
    {
        _fuzzyDurationToleranceSeconds = Math.Max(0, fuzzyDurationToleranceSeconds);
        foreach (var song in songs)
        {
            var pathKey = NavidromeLikeMatcher.NavPathKey(song);
            if (pathKey is not null)
                Add(_byPath, pathKey, song);

            if (!string.IsNullOrWhiteSpace(song.MusicBrainzId))
                Add(_byMbid, song.MusicBrainzId!, song);

            var atKey = ArtistTitleKey(song.Artist, song.Title);
            if (atKey is not null)
                Add(_byArtistTitle, atKey, song);
        }
    }

    /// <summary>All Navidrome songs matching this MusicHoarder song, strongest tier only (empty if none).</summary>
    public IReadOnlyList<NavidromeSong> Find(LikeMatchKey key)
    {
        // 1) library-relative path (destination first, then source).
        var byPath = LookupPath(key.DestinationRelPath).Concat(LookupPath(key.SourceRelPath))
            .DistinctBy(s => s.Id).ToList();
        if (byPath.Count > 0)
            return byPath;

        // 2) MusicBrainz recording id.
        if (key.MusicBrainzId is not null && _byMbid.TryGetValue(key.MusicBrainzId, out var byMbid))
            return byMbid;

        // 3) fuzzy: normalized artist+title, bounded by duration tolerance.
        var atKey = key.NormalizedArtist is not null && key.NormalizedTitle is not null
            ? $"{key.NormalizedArtist}\t{key.NormalizedTitle}"
            : null;
        if (atKey is not null && _byArtistTitle.TryGetValue(atKey, out var byAt))
        {
            var withinDuration = byAt.Where(s => DurationMatches(key.DurationSeconds, s.DurationSeconds)).ToList();
            if (withinDuration.Count > 0)
                return withinDuration;
        }

        return [];
    }

    private IEnumerable<NavidromeSong> LookupPath(string? relPath)
        => relPath is not null && _byPath.TryGetValue(relPath, out var list) ? list : [];

    private bool DurationMatches(int? a, int? b)
    {
        if (a is null || b is null) return true; // unknown duration doesn't veto a name match
        return Math.Abs(a.Value - b.Value) <= _fuzzyDurationToleranceSeconds;
    }

    private static string? ArtistTitleKey(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
        var a = TitleNormalizer.NormalizeForSearch(artist);
        var t = TitleNormalizer.NormalizeForSearch(title);
        return string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(t) ? null : $"{a}\t{t}";
    }

    private static void Add(Dictionary<string, List<NavidromeSong>> map, string key, NavidromeSong song)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(song);
    }
}
