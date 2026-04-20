namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Parsed Spotify track from catalog search (client-credentials).
/// </summary>
public record SpotifyCatalogTrack(
    string Id,
    string Title,
    string Artist,
    string AlbumName,
    int? ReleaseYear,
    int? TrackNumber,
    int DurationMs,
    string? Isrc);
