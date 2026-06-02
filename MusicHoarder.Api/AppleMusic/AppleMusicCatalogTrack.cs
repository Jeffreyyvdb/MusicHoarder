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

/// <summary>An iTunes album (collection) with its full tracklist (from <c>GET /lookup?id=..&amp;entity=song</c>).</summary>
public record AppleAlbumDetail(
    string Id,
    string? Name,
    string? Artist,
    int? Year,
    string? ArtworkUrl,
    IReadOnlyList<AppleAlbumTrackItem> Tracks);

public record AppleAlbumTrackItem(
    int DiscNumber,
    int TrackNumber,
    string? Title,
    int DurationMs,
    string? Id);
