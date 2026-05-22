namespace MusicHoarder.Api.AppleMusic;

/// <summary>
/// Parsed iTunes Search API song result (no auth). Shaped like <c>SpotifyCatalogTrack</c> so
/// provider scoring is identical; iTunes carries no ISRC, so <see cref="Isrc"/> is always null.
/// </summary>
public record AppleMusicCatalogTrack(
    string Id,
    string Title,
    string Artist,
    string AlbumName,
    int? ReleaseYear,
    int? TrackNumber,
    int DurationMs,
    string? Isrc);
