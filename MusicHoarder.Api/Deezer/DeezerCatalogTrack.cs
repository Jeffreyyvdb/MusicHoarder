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
    string? Isrc,
    /// <summary>Discrete contributor names (';'-joined), from the full track detail's
    /// <c>contributors</c> array; null on search results and single-artist payloads.</summary>
    string? Artists = null);

/// <summary>A Deezer album with its full tracklist (from <c>GET /album/{id}</c>).</summary>
public record DeezerAlbumDetail(
    string Id,
    string? Title,
    string? Artist,
    int? Year,
    string? CoverUrl,
    IReadOnlyList<DeezerAlbumTrackItem> Tracks);

public record DeezerAlbumTrackItem(
    int DiscNumber,
    int TrackNumber,
    string? Title,
    int DurationMs,
    string? Id);
