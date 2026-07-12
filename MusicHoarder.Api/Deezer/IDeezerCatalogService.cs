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

    // --- Discover (editorial browse) ---

    /// <summary>Editorial genres for the discover browser (<c>GET /genre</c>), cached ~10 min.</summary>
    Task<IReadOnlyList<DeezerGenre>> GetGenresAsync(CancellationToken ct = default);

    /// <summary>Chart (top) playlists, optionally scoped to a genre (<c>GET /chart/{genreId}/playlists</c>).
    /// A null genre uses the global chart (genre id 0). Cached ~10 min.</summary>
    Task<IReadOnlyList<DeezerPlaylistSummary>> GetChartPlaylistsAsync(long? genreId, int limit, CancellationToken ct = default);

    /// <summary>Playlist search (<c>GET /search/playlist</c>). Cached ~10 min.</summary>
    Task<IReadOnlyList<DeezerPlaylistSummary>> SearchPlaylistsAsync(string query, int limit, CancellationToken ct = default);

    /// <summary>Full playlist metadata incl. checksum (<c>GET /playlist/{id}</c>); null if not found.</summary>
    Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default);

    /// <summary>Tracks of a playlist (<c>GET /playlist/{id}/tracks</c>), paged to completion unless
    /// <paramref name="maxTracks"/> caps the fetch. Lightweight (no ISRC); hydrate a track with
    /// <see cref="LookupByIdAsync"/> only when about to insert it. The result's
    /// <see cref="DeezerPlaylistTracksResult.IsComplete"/> is false when a page failed mid-run or the cap
    /// was hit — callers persisting a checksum must only advance it on a complete fetch.</summary>
    Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default);
}
