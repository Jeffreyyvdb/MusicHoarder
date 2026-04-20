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
}
