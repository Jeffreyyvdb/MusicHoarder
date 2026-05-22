namespace MusicHoarder.Api.Deezer;

public interface IDeezerCatalogService
{
    /// <summary>Exact lookup by ISRC (<c>GET /track/isrc:{isrc}</c>); null when not found.</summary>
    Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default);

    /// <summary>Fuzzy artist+title search (<c>GET /search</c>). Items are lightweight: no ISRC,
    /// release year or track position (those come from <see cref="LookupByIdAsync"/>).</summary>
    Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default);

    /// <summary>Hydrate a search hit to full detail (<c>GET /track/{id}</c>): ISRC, release year,
    /// track position.</summary>
    Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default);
}
