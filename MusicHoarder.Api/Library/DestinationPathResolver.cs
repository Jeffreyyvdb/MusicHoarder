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

    public string ResolvePath(SongMetadata song)
    {
        ArgumentNullException.ThrowIfNull(song);

        var title = NormalizeSegment(song.Title, "Unknown Title");
        var extension = NormalizeExtension(song.Extension);

        // Compilations route under a single "Various Artists" tree (keyed by album, not the
        // per-track artist) so the album stays together — same convention every server uses.
        var topFolder = song.IsCompilation
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
}
