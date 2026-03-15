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

    public string ResolvePath(SongMetadata song)
    {
        ArgumentNullException.ThrowIfNull(song);

        var albumArtist = ResolveAlbumArtist(song);
        var title = NormalizeSegment(song.Title, "Unknown Title");
        var extension = NormalizeExtension(song.Extension);

        if (song.IsUnreleased)
        {
            return Path.Combine(
                _destinationRoot,
                albumArtist,
                "Unreleased",
                $"{title}{extension}");
        }

        var album = NormalizeSegment(song.Album, "Unknown Album");
        var albumFolder = song.Year is > 0
            ? $"{song.Year.Value} - {album}"
            : album;

        var trackPrefix = song.TrackNumber is > 0
            ? $"{song.TrackNumber.Value:00} - "
            : string.Empty;

        var fileName = $"{trackPrefix}{title}{extension}";

        return Path.Combine(_destinationRoot, albumArtist, albumFolder, fileName);
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
