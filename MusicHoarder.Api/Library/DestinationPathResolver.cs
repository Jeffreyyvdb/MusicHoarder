using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

public class DestinationPathResolver(IOptions<MusicEnricherOptions> options) : IDestinationPathResolver
{
    private const int MaxSegmentLength = 60;
    private static readonly char[] ForbiddenPathChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string _destinationRoot = options.Value.DestinationDirectory;
    private readonly string _compilationFolder = string.IsNullOrWhiteSpace(options.Value.CompilationFolderName)
        ? "Various Artists"
        : options.Value.CompilationFolderName;

    public string ResolvePath(SongMetadata song) => ResolvePath(song, null);

    /// <summary>
    /// Resolves the destination path for a song, deriving the album-IDENTITY folder segments
    /// (top/album-artist folder + "year - album" folder) from the reconciled
    /// <paramref name="albumIdentity"/> when one is supplied, and the track-level file name from the
    /// song. Passing the elected identity is what makes an album's folder DETERMINISTIC across build
    /// runs: it no longer depends on the individual track's (drifting) album-artist / album / year, so
    /// every track of one logical album resolves to the same folder regardless of which tracks are in
    /// the current batch — closing the chicken-and-egg where the reconciliation grouped on a folder path
    /// that itself depended on the unstable per-song album-artist. With <c>null</c> it reproduces the
    /// pre-reconciliation per-song routing exactly.
    /// </summary>
    public string ResolvePath(SongMetadata song, AlbumIdentity? albumIdentity)
    {
        ArgumentNullException.ThrowIfNull(song);

        var title = NormalizeSegment(song.Title, "Unknown Title");
        var extension = NormalizeExtension(song.Extension);

        // Album-identity fields come from the elected identity when present, else the song itself.
        var isCompilation = albumIdentity?.IsCompilation ?? song.IsCompilation;
        var albumArtist = albumIdentity is not null ? albumIdentity.AlbumArtist : song.AlbumArtist;
        var albumTitle = albumIdentity is not null ? albumIdentity.Album : song.Album;
        var year = albumIdentity is not null ? albumIdentity.Year : song.Year;

        var topFolder = IsVariousArtists(isCompilation, albumArtist)
            ? NormalizeSegment(_compilationFolder, "Various Artists")
            : ResolveAlbumArtistSegment(albumArtist, song.Artist);

        if (song.IsUnreleased)
        {
            return Path.Combine(
                _destinationRoot,
                topFolder,
                "Unreleased",
                $"{title}{extension}");
        }

        var album = NormalizeSegment(albumTitle, "Unknown Album");
        var albumFolder = year is > 0
            ? $"{year.Value} - {album}"
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

    private static string ResolveAlbumArtistSegment(string? albumArtist, string? trackArtist)
    {
        var preferred = albumArtist ?? ArtistCreditNormalizer.GetPrimaryArtist(trackArtist) ?? trackArtist;
        return NormalizeSegment(preferred, "Unknown Artist");
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
        => IsVariousArtists(song.IsCompilation, song.AlbumArtist);

    /// <summary>
    /// Overload taking the album-identity fields directly, so the reconciled identity's compilation
    /// flag / album-artist can drive the Various-Artists routing decision (not just the per-song row).
    /// </summary>
    public static bool IsVariousArtists(bool isCompilation, string? albumArtist)
        => isCompilation
            && (string.IsNullOrWhiteSpace(albumArtist) || IsVariousArtistsSentinel(albumArtist));

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
