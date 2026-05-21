using System.Text.RegularExpressions;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Resolves the artist/title that drive a name-based provider's search query. Embedded tags
/// win; when they're missing we derive a best-effort guess from the source file path so
/// untagged files (leaks, "loose downloads") still get a provider attempt instead of
/// dead-ending silently in <see cref="EnrichmentStatus.Pending"/>.
/// <para>
/// Path-derived values are used for <b>querying only</b> — they are never written onto the song
/// row. A provider must still return a confident match (scored against whatever real signal
/// exists) before any metadata is applied, so a wrong directory guess just yields a NoMatch
/// that surfaces the track for review rather than corrupting it.
/// </para>
/// </summary>
public static partial class SongSearchText
{
    /// <param name="sourceRoot">
    /// The configured library source directory. When supplied, the first path segment beneath it
    /// is treated as the artist (matching the <c>&lt;Artist&gt;/&lt;Album&gt;/&lt;track&gt;</c>
    /// layout the scanner indexes). Optional so callers without the option can still get a
    /// best-effort guess.
    /// </param>
    public static (string? Artist, string? Title) Resolve(SongMetadata song, string? sourceRoot = null)
    {
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? null : song.Artist!.Trim();
        var title = string.IsNullOrWhiteSpace(song.Title) ? null : song.Title!.Trim();

        if (artist is not null && title is not null)
            return (artist, title);

        var (pathArtist, pathTitle) = FromPath(song.SourcePath, sourceRoot);
        return (artist ?? pathArtist, title ?? pathTitle);
    }

    /// <summary>Whether a name-based provider has enough to attempt a search (tags or path-derived).</summary>
    public static bool HasSearchableText(SongMetadata song, string? sourceRoot = null)
    {
        var (artist, title) = Resolve(song, sourceRoot);
        return !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title);
    }

    private static (string? Artist, string? Title) FromPath(string? sourcePath, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return (null, null);

        var relative = StripRoot(sourcePath, sourceRoot);
        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return (null, null);

        var title = CleanTrackName(StripExtension(segments[^1]));

        // The first directory under the source root is the artist (<Artist>/<Album?>/<track>).
        // segments[0] is only the artist when there's at least one directory above the file.
        string? artist = segments.Length >= 2 ? segments[0] : null;
        artist = string.IsNullOrWhiteSpace(artist) ? null : artist!.Trim();

        return (artist, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private static string StripRoot(string sourcePath, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            // No root context: assume the conventional /<root...>/<Artist>/<...>/<track> shape and
            // drop everything up to and including the parent of the artist by keeping the last
            // three segments (artist / album / track) when the path is deep enough.
            var all = sourcePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            return all.Length >= 3 ? string.Join('/', all[^3..]) : sourcePath;
        }

        var normPath = sourcePath.Replace('\\', '/');
        var normRoot = sourceRoot.Replace('\\', '/').TrimEnd('/');
        if (normPath.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase))
            return normPath[(normRoot.Length + 1)..];

        return normPath;
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>Strips a leading track/disc number prefix ("04 ", "01 - ", "1-01.", "01.") and tidies separators.</summary>
    private static string CleanTrackName(string name)
    {
        var cleaned = TrackNumberPrefix().Replace(name, "");
        cleaned = cleaned.Replace('_', ' ');
        cleaned = WhitespacePattern().Replace(cleaned, " ").Trim();
        return cleaned.Length == 0 ? name.Trim() : cleaned;
    }

    [GeneratedRegex(@"^\s*(\d{1,2}-)?\d{1,3}\s*[.\-_]?\s+", RegexOptions.Compiled)]
    private static partial Regex TrackNumberPrefix();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();
}
