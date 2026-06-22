using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

public class DestinationPathResolver(IOptions<MusicEnricherOptions> options) : IDestinationPathResolver
{
    private const int MaxSegmentLength = 60;
    private static readonly char[] ForbiddenPathChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string _destinationRoot = options.Value.DestinationDirectory;
    private readonly string? _sourceRoot = options.Value.SourceDirectory;
    private readonly string _compilationFolder = string.IsNullOrWhiteSpace(options.Value.CompilationFolderName)
        ? "Various Artists"
        : options.Value.CompilationFolderName;

    public string ResolvePath(SongMetadata song)
    {
        ArgumentNullException.ThrowIfNull(song);

        var title = NormalizeSegment(ResolveTitle(song), "Unknown Title");
        var extension = NormalizeExtension(song.Extension);

        var topFolder = IsVariousArtists(song)
            ? NormalizeSegment(_compilationFolder, "Various Artists")
            : ResolveAlbumArtist(song);

        if (song.IsUnreleased)
        {
            return Path.Combine(
                _destinationRoot,
                topFolder,
                "Unreleased",
                $"{title}{extension}");
        }

        var album = NormalizeSegment(song.Album, "Unknown Album");
        var albumFolder = song.Year is > 0
            ? $"{song.Year.Value} - {album}"
            : album;

        var fileName = $"{BuildTrackPrefix(song)}{title}{extension}";

        return Path.Combine(_destinationRoot, topFolder, albumFolder, fileName);
    }

    // Multi-disc albums prefix the disc number so disc 2's "01" doesn't collide with disc 1's "01"
    // within a single album folder; single-disc albums keep the plain "NN - " prefix.
    private static string BuildTrackPrefix(SongMetadata song)
    {
        if (song.TrackNumber is not > 0)
        {
            return string.Empty;
        }

        return song.TotalDiscs is > 1 && song.DiscNumber is > 0
            ? $"{song.DiscNumber.Value}-{song.TrackNumber.Value:00} - "
            : $"{song.TrackNumber.Value:00} - ";
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim();
        foreach (var forbiddenChar in ForbiddenPathChars)
        {
            cleaned = cleaned.Replace(forbiddenChar.ToString(), string.Empty, StringComparison.Ordinal);
        }

        return cleaned;
    }

    private static string NormalizeSegment(string? value, string fallback)
    {
        var sanitized = Sanitize(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        return Truncate(sanitized);
    }

    private static string NormalizeExtension(string? extension)
    {
        var sanitized = Sanitize(extension ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        return sanitized.StartsWith(".", StringComparison.Ordinal)
            ? sanitized
            : $".{sanitized}";
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxSegmentLength
            ? value
            : value[..MaxSegmentLength];
    }

    private static string ResolveAlbumArtist(SongMetadata song)
    {
        var preferred = song.AlbumArtist ?? ArtistCreditNormalizer.GetPrimaryArtist(song.Artist) ?? song.Artist;
        return NormalizeSegment(preferred, "Unknown Artist");
    }

    /// <summary>
    /// The enriched title when present; otherwise the title parsed from the source filename so an
    /// untagged/unmatched file (a leak whose title lives only in its filename, e.g.
    /// "999 (Triple 9).mp3") lands as that name on disk instead of the "Unknown Title" placeholder.
    /// Filename-derived only — it's never written back onto the row, just used for the destination path.
    /// </summary>
    private string? ResolveTitle(SongMetadata song)
    {
        if (!string.IsNullOrWhiteSpace(song.Title))
            return song.Title;

        return SongSearchText.ResolveDetailed(song, _sourceRoot).Title;
    }

    /// <summary>
    /// Genuine various-artists compilations route under a single "Various Artists" tree (keyed by
    /// album, not the per-track artist) so the album stays together — same convention every server
    /// uses. But a single-artist release that merely happens to be flagged "compilation" (e.g. a
    /// greatest-hits a provider tagged compilation) must still file under that artist, not get
    /// exiled to Various Artists. Treat it as various-artists only when the album artist is absent
    /// or is itself a various-artists sentinel. Shared with <see cref="AlbumGroupKey"/> so the
    /// logical-album grouping and the folder routing can never disagree.
    /// </summary>
    public static bool IsVariousArtists(SongMetadata song)
        => song.IsCompilation
            && (string.IsNullOrWhiteSpace(song.AlbumArtist) || IsVariousArtistsSentinel(song.AlbumArtist));

    /// <summary>
    /// The album-artist values providers use for genuine multi-artist releases. A track whose album
    /// artist is one of these is a true compilation and belongs under the Various Artists tree.
    /// </summary>
    public static bool IsVariousArtistsSentinel(string albumArtist)
    {
        var trimmed = albumArtist.Trim();
        return trimmed.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Various", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("VA", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("V.A.", StringComparison.OrdinalIgnoreCase);
    }
}
