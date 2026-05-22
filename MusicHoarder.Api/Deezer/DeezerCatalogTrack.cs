namespace MusicHoarder.Api.Deezer;

/// <summary>
/// Parsed Deezer track from catalog search / track lookup (no auth).
/// Shaped like <c>SpotifyCatalogTrack</c> so provider scoring is identical; Deezer reports
/// duration in seconds, which the service converts to <see cref="DurationMs"/>.
/// </summary>
public record DeezerCatalogTrack(
    string Id,
    string Title,
    string Artist,
    string AlbumName,
    int? ReleaseYear,
    int? TrackNumber,
    int DurationMs,
    string? Isrc);
