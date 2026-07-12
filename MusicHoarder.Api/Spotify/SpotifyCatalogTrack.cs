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
    string? Isrc,
    string? Artists = null,
    int? DiscNumber = null,
    string? AlbumType = null,
    int? TotalTracks = null,
    string? AlbumId = null);

/// <summary>A Spotify album with its full tracklist (from <c>GET /v1/albums/{id}</c>).</summary>
public record SpotifyAlbumDetail(
    string Id,
    string? Name,
    string? Artist,
    int? Year,
    string? ImageUrl,
    IReadOnlyList<SpotifyAlbumTrackItem> Tracks,
    // Descriptive album-level fields. Copyright is the © line (the ℗ phonogram line is dropped);
    // Spotify is the only source MusicHoarder has for it.
    string? ReleaseDate = null,
    string? Label = null,
    string? Upc = null,
    string? Copyright = null);

public record SpotifyAlbumTrackItem(
    int DiscNumber,
    int TrackNumber,
    string? Title,
    int DurationMs,
    string? Id);
