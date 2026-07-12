namespace MusicHoarder.Api.Spotify;

public interface ISpotifyCatalogSearchService
{
    /// <summary>
    /// Search tracks using client-credentials (app token cached in memory only).
    /// </summary>
    Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracksAsync(
        string clientId,
        string clientSecret,
        string query,
        CancellationToken ct = default);

    /// <summary>Resolves the album id that a track belongs to (<c>GET /v1/tracks/{id}</c>).</summary>
    Task<string?> GetTrackAlbumIdAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default);

    /// <summary>Finds an album id by artist + album name (<c>GET /v1/search?type=album</c>).</summary>
    Task<string?> SearchAlbumIdAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default);

    /// <summary>Fetches an album with its full tracklist (<c>GET /v1/albums/{id}</c>); null if not found.</summary>
    Task<SpotifyAlbumDetail?> GetAlbumAsync(string clientId, string clientSecret, string albumId, CancellationToken ct = default);

    /// <summary>Resolves a Spotify track id for the given ISRC (<c>GET /v1/search?q=isrc:{isrc}</c>);
    /// null when no track matches or credentials are missing.</summary>
    Task<string?> SearchTrackIdByIsrcAsync(string clientId, string clientSecret, string isrc, CancellationToken ct = default);
}
