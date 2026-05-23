using System.Text.RegularExpressions;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Resolves the artist/album/title that drive a name-based provider's search query. Embedded
/// tags win; when they're missing we derive a best-effort guess from the source file path so
/// untagged files (leaks, "loose downloads") still get a provider attempt instead of
/// dead-ending silently in <see cref="EnrichmentStatus.Pending"/>. The path is read against the
/// conventional <c>&lt;Artist&gt;/&lt;Album&gt;/NN Title.ext</c> layout that Navidrome (and most
/// libraries) recommend.
/// <para>
/// Path-derived values are used for <b>querying and scoring only</b> — they are never written
/// onto the song row. A provider must still return a confident match (scored against whatever
/// real signal exists) before any metadata is applied, so a wrong directory guess just yields a
/// NoMatch that surfaces the track for review rather than corrupting it.
/// </para>
/// </summary>
public static partial class SongSearchText
{
    /// <summary>Effective search signal for a song: embedded tags, falling back to the file path.</summary>
    public readonly record struct Resolved(string? Artist, string? Album, string? Title, int? TrackNumber);

    /// <param name="sourceRoot">
    /// The configured library source directory. When supplied, the first path segment beneath it
    /// is treated as the artist and the directory immediately containing the file as the album
    /// (matching the <c>&lt;Artist&gt;/&lt;Album&gt;/&lt;track&gt;</c> layout the scanner indexes).
    /// Optional so callers without the option can still get a best-effort guess.
    /// </param>
    public static Resolved ResolveDetailed(SongMetadata song, string? sourceRoot = null)
    {
        var artist = Clean(song.Artist);
        var album = Clean(song.Album);
        var title = Clean(song.Title);
        var trackNumber = song.TrackNumber is int t && t > 0 ? t : (int?)null;

        // Only touch the path for fields the tags didn't supply.
        if (artist is not null && album is not null && title is not null && trackNumber is not null)
            return new Resolved(artist, album, title, trackNumber);

        var fromPath = FromPath(song.SourcePath, sourceRoot);
        return new Resolved(
            artist ?? fromPath.Artist,
            album ?? fromPath.Album,
            title ?? fromPath.Title,
            trackNumber ?? fromPath.TrackNumber);
    }

    /// <summary>Artist/title the name-based providers query on (tags win, else path-derived).</summary>
    public static (string? Artist, string? Title) Resolve(SongMetadata song, string? sourceRoot = null)
    {
        var resolved = ResolveDetailed(song, sourceRoot);
        return (resolved.Artist, resolved.Title);
    }

    /// <summary>Whether a name-based provider has enough to attempt a search (tags or path-derived).</summary>
    public static bool HasSearchableText(SongMetadata song, string? sourceRoot = null)
    {
        var resolved = ResolveDetailed(song, sourceRoot);
        return !string.IsNullOrWhiteSpace(resolved.Artist) && !string.IsNullOrWhiteSpace(resolved.Title);
    }

    private static Resolved FromPath(string? sourcePath, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return default;

        var relative = StripRoot(sourcePath, sourceRoot);
        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return default;

        var (title, trackNumber) = ParseTrackName(StripExtension(segments[^1]));

        // The first directory under the source root is the artist, the one directly containing the
        // file is the album (<Artist>/<Album?>/<track>). Each only exists when the path is deep
        // enough to have that directory level above the file.
        string? artist = segments.Length >= 2 ? Clean(segments[0]) : null;
        string? album = segments.Length >= 3 ? Clean(segments[^2]) : null;

        // Compilation folders ("Various Artists", "VA") name the album, not the performer — keep the
        // album hint but drop the bogus artist so providers don't search for an artist named "Various".
        if (artist is not null && IsCompilationMarker(artist))
            artist = null;

        title = StripArtistPrefix(title, artist);

        return new Resolved(artist, album, string.IsNullOrWhiteSpace(title) ? null : title, trackNumber);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsCompilationMarker(string artist)
        => artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
            || artist.Equals("Various Artist", StringComparison.OrdinalIgnoreCase)
            || artist.Equals("Various", StringComparison.OrdinalIgnoreCase)
            || artist.Equals("VA", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Splits a "NN - Title" file stem into its track-number prefix ("04 ", "01 - ", "1-01.",
    /// "01.") and the cleaned title. The trailing number of a disc-track prefix ("1-01") is the
    /// track; a bare leading number is the track.
    /// </summary>
    private static (string Title, int? TrackNumber) ParseTrackName(string name)
    {
        int? trackNumber = null;
        var prefix = TrackNumberPrefix().Match(name);
        if (prefix.Success && prefix.Groups["track"].Success
            && int.TryParse(prefix.Groups["track"].Value, out var n) && n > 0)
        {
            trackNumber = n;
        }

        var cleaned = TrackNumberPrefix().Replace(name, "");
        cleaned = cleaned.Replace('_', ' ');
        cleaned = WhitespacePattern().Replace(cleaned, " ").Trim();
        return (cleaned.Length == 0 ? name.Trim() : cleaned, trackNumber);
    }

    /// <summary>
    /// Drops a leading "Artist - " from a path-derived title when the lead segment is the artist
    /// we already know (e.g. "Juice - Benjamin" under a "Juice WRLD" folder → "Benjamin"). Many
    /// loose downloads follow the "Artist - Title" filename convention, which otherwise pollutes
    /// the provider search with the artist name. The separator may be an ASCII hyphen or any common
    /// Unicode dash (en/em dash etc.) since download filenames vary. Gated on the lead actually
    /// matching the artist so titles that legitimately contain a dash (e.g. "Robbery - Live") are
    /// left untouched, and only applied when an artist is known.
    /// </summary>
    private static string StripArtistPrefix(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return title;

        var sep = DashSeparator().Match(title);
        if (!sep.Success)
            return title;

        var lead = title[..sep.Index].Trim();
        var rest = title[(sep.Index + sep.Length)..].Trim();
        if (rest.Length == 0)
            return title;

        var leadNorm = TitleNormalizer.NormalizeForSearch(lead);
        var artistNorm = TitleNormalizer.NormalizeForSearch(artist);
        if (leadNorm.Length == 0 || artistNorm.Length == 0)
            return title;

        // Strip when the lead is (an abbreviation of) the artist: equal, or either a prefix of the
        // other ("Juice" ⊂ "Juice WRLD", and the full "Juice WRLD - Benjamin" form).
        var matchesArtist = leadNorm == artistNorm
            || artistNorm.StartsWith(leadNorm, StringComparison.Ordinal)
            || leadNorm.StartsWith(artistNorm, StringComparison.Ordinal);

        return matchesArtist ? rest : title;
    }

    [GeneratedRegex(@"^\s*(\d{1,2}-)?(?<track>\d{1,3})\s*[.\-_]?\s+", RegexOptions.Compiled)]
    private static partial Regex TrackNumberPrefix();

    // An "Artist - Title" separator: a single hyphen/dash (ASCII hyphen-minus, Unicode hyphen
    // through horizontal bar, or the minus sign) with whitespace on at least one side, so genuine
    // hyphenated words ("Anti-Hero") aren't split.
    [GeneratedRegex(@"(?:\s+[-‐-―−]\s*|\s*[-‐-―−]\s+)", RegexOptions.Compiled)]
    private static partial Regex DashSeparator();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();
}
