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

    /// <summary>Finds an album id by artist + album name (<c>GET /search/album</c>).</summary>
    Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default);

    /// <summary>Fetches an album with its full tracklist (<c>GET /album/{id}</c>); null if not found.</summary>
    Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default);
}
