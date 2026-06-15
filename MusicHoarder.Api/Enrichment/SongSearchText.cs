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
    /// <remarks>
    /// The four positional fields keep their original arity (and <c>Deconstruct</c>) so existing
    /// consumers are unaffected. The <c>*FromPath</c> flags record <b>provenance</b>: when a field was
    /// guessed from the file path rather than read from an embedded tag, a provider must not treat a
    /// disagreement as the file contradicting its own identity (no blocking warning) — see
    /// <see cref="Matching.MatchWarnings"/> and the scorers. <see cref="RawSearchText"/> is the cleaned
    /// filename free-text the providers query on when the identity is path-derived, so a messy
    /// "20-luie_mannen-hef_(prod_by_dj_mp).mp3" still searches as "luie mannen hef".
    /// </remarks>
    public readonly record struct Resolved(string? Artist, string? Album, string? Title, int? TrackNumber)
    {
        /// <summary>True when <see cref="Artist"/> was derived from the path, not an embedded tag.</summary>
        public bool ArtistFromPath { get; init; }
        /// <summary>True when <see cref="Title"/> was derived from the path, not an embedded tag.</summary>
        public bool TitleFromPath { get; init; }
        /// <summary>True when <see cref="Album"/> was derived from the path, not an embedded tag.</summary>
        public bool AlbumFromPath { get; init; }
        /// <summary>Cleaned free-text query built from the filename (track #, underscores and
        /// "(prod …)"/"(feat …)" credits stripped, dashes folded to spaces); null when the path
        /// yields nothing usable. Providers query on this when artist/title are path-derived.</summary>
        public string? RawSearchText { get; init; }

        /// <summary>Whether the artist or title came from the path rather than an embedded tag.</summary>
        public bool IdentityFromPath => ArtistFromPath || TitleFromPath;

        /// <summary>
        /// True when the filename itself encodes the artist ("Artist - Title"). In that case the
        /// containing folder is a download-tool/bucket name ("slskd", an A–Z bucket), not the performer,
        /// so it must be kept OUT of the search query — a junk token like "slskd" measurably degrades a
        /// real search engine's ranking.
        /// </summary>
        public bool FilenameCarriesArtist { get; init; }

        /// <summary>Artist parsed from an "Artist - Title" filename (set with <see cref="FilenameCarriesArtist"/>).
        /// Lets a fielded-search provider (MusicBrainz) query precise artist/title instead of free-text —
        /// MusicBrainz ranks a bare free-text query poorly. Distinct from the positional <see cref="Artist"/>,
        /// which on a bucket layout is the junk folder.</summary>
        public string? SplitArtist { get; init; }
        /// <summary>Title parsed from an "Artist - Title" filename (see <see cref="SplitArtist"/>).</summary>
        public string? SplitTitle { get; init; }

        /// <summary>
        /// The free-text query a provider should use when the identity is path-derived. When the filename
        /// carries the artist ("Artist - Title", the loose-download convention) we query on the cleaned
        /// filename ALONE — never the junk bucket folder. Only a structured
        /// "&lt;Artist&gt;/&lt;Album&gt;/NN Title" file (bare-title filename) prepends its folder-artist so
        /// the artist isn't lost. Falls back to <see cref="Artist"/> when there's no filename text.
        /// </summary>
        public string? PathQuery
        {
            get
            {
                if (string.IsNullOrWhiteSpace(RawSearchText))
                    return Artist;
                if (!FilenameCarriesArtist
                    && !string.IsNullOrWhiteSpace(Artist)
                    && Matching.CandidateTextMatch.Containment(Artist, RawSearchText) < 1.0)
                    return $"{Artist} {RawSearchText}";
                return RawSearchText;
            }
        }
    }

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
            trackNumber ?? fromPath.TrackNumber)
        {
            // Provenance: a field is "from path" only when the tag was missing and the path supplied it.
            ArtistFromPath = artist is null && fromPath.Artist is not null,
            TitleFromPath = title is null && fromPath.Title is not null,
            AlbumFromPath = album is null && fromPath.Album is not null,
            RawSearchText = fromPath.RawSearchText,
            FilenameCarriesArtist = fromPath.FilenameCarriesArtist,
            SplitArtist = fromPath.SplitArtist,
            SplitTitle = fromPath.SplitTitle,
        };
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

        var stem = StripExtension(segments[^1]);
        var (title, trackNumber) = ParseTrackName(stem);

        // Free-text query the providers fall back to when the identity is path-derived: the filename
        // is the most deliberately-encoded signal, so we clean it (track #, underscores, "(prod …)"
        // credits, dashes) and let the search engine parse it rather than committing to a positional
        // artist/title split that breaks on "20-luie_mannen-hef_(prod_by_dj_mp).mp3".
        var rawSearchText = CleanFilenameForSearch(stem);

        // "Artist - Title" filename → the artist is in the filename, so the containing folder is a
        // download-tool/bucket name to keep out of the query. Detected on the track-stripped stem; the
        // split also feeds MusicBrainz a fielded artist/title query (free-text ranks poorly there).
        var fileSplit = SplitArtistTitle(title);
        var filenameCarriesArtist = fileSplit is not null;

        // The first directory under the source root is the artist, the one directly containing the
        // file is the album (<Artist>/<Album?>/<track>). Each only exists when the path is deep
        // enough to have that directory level above the file.
        string? artist = segments.Length >= 2 ? Clean(segments[0]) : null;
        string? album = segments.Length >= 3 ? Clean(segments[^2]) : null;

        // Compilation folders ("Various Artists", "VA") name the album, not the performer — keep the
        // album hint but drop the bogus artist so providers don't search for an artist named "Various".
        if (artist is not null && IsCompilationMarker(artist))
            artist = null;

        // Loose-download convention: a file sitting exactly one directory under the source root, named
        // "Artist - Title" and not track-numbered, carries its artist in the filename — the containing
        // folder is a download-tool/category name ("slskd", "Soulseek", "Leaks"), not the performer.
        // Structured libraries instead encode the artist as a folder (<Artist>/<Album>/NN Title) and
        // number their tracks, so this never fires for them.
        if (trackNumber is null && segments.Length == 2 && fileSplit is { } split)
            return new Resolved(split.Artist, album, split.Title, trackNumber)
            {
                RawSearchText = rawSearchText,
                FilenameCarriesArtist = true,
                SplitArtist = split.Artist,
                SplitTitle = split.Title,
            };

        title = StripArtistPrefix(title, artist);

        return new Resolved(artist, album, string.IsNullOrWhiteSpace(title) ? null : title, trackNumber)
        {
            RawSearchText = rawSearchText,
            FilenameCarriesArtist = filenameCarriesArtist,
            SplitArtist = fileSplit?.Artist,
            SplitTitle = fileSplit?.Title,
        };
    }

    /// <summary>
    /// Cleans a filename stem into a free-text search query: underscores to spaces, leading
    /// track-number prefix removed ("20-", "05 ", "01."), parenthetical production credits
    /// ("(prod by …)", "(produced by …)") dropped, and every dash folded to a space so multi-dash
    /// names ("luie_mannen-hef") tokenise cleanly. Returns null when nothing usable remains.
    /// </summary>
    private static string? CleanFilenameForSearch(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return null;

        var text = stem.Replace('_', ' ');
        text = LeadingTrackNumber().Replace(text, "");
        text = ProductionCredit().Replace(text, " ");
        text = AnyDash().Replace(text, " ");
        text = WhitespacePattern().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
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

    /// <summary>
    /// Splits a "Artist - Title" file stem on the first dash separator. Returns <c>null</c> when there
    /// is no separator or either side is empty (so a leading/trailing dash isn't mistaken for a split).
    /// </summary>
    private static (string Artist, string Title)? SplitArtistTitle(string stem)
    {
        var sep = DashSeparator().Match(stem);
        if (!sep.Success)
            return null;

        var artist = stem[..sep.Index].Trim();
        var title = stem[(sep.Index + sep.Length)..].Trim();
        if (artist.Length == 0 || title.Length == 0)
            return null;

        return (artist, title);
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

    // A leading track-number on a free-text query stem: 1–3 digits followed by a dash/dot/space
    // separator ("20-", "05 ", "01. "). Bounded to 3 digits so a year/number title ("1979") survives.
    [GeneratedRegex(@"^\s*\d{1,3}\s*[-.\s]\s*", RegexOptions.Compiled)]
    private static partial Regex LeadingTrackNumber();

    // A parenthetical production credit ("(prod by …)", "(produced by …)", "(prod. …)") — pure search
    // noise. Featuring credits are intentionally kept; they sharpen the search.
    [GeneratedRegex(@"\((?:prod|produced|directed|dir)\b[^)]*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ProductionCredit();

    // Any dash variant, folded to a space for free-text querying/tokenising (unlike DashSeparator this
    // does not require surrounding whitespace, so "mannen-hef" becomes "mannen hef").
    [GeneratedRegex(@"[-‐-―−]", RegexOptions.Compiled)]
    private static partial Regex AnyDash();
}
