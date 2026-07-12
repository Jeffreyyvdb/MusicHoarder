using MusicHoarder.Api.Deezer;

namespace MusicHoarder.Api.Tests.Deezer;

/// <summary>
/// Configurable in-memory <see cref="IDeezerCatalogService"/> test double. Only the members a test
/// exercises need to be set; the rest return empty/null defaults.
/// </summary>
public sealed class FakeDeezerCatalogService : IDeezerCatalogService
{
    public Dictionary<string, DeezerPlaylistSummary> Playlists { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<DeezerPlaylistTrack>> PlaylistTracks { get; } = new(StringComparer.Ordinal);

    /// <summary>Playlist ids whose tracklist fetch simulates a mid-run page failure (IsComplete = false).</summary>
    public HashSet<string> IncompletePlaylistTracks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DeezerCatalogTrack> TracksById { get; } = new(StringComparer.Ordinal);
    public List<DeezerGenre> Genres { get; } = [];
    public List<DeezerPlaylistSummary> ChartPlaylists { get; } = [];
    public List<DeezerPlaylistSummary> SearchResults { get; } = [];

    public Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default) =>
        Task.FromResult<DeezerCatalogTrack?>(null);

    public Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeezerCatalogTrack>>([]);

    public Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(TracksById.GetValueOrDefault(id));

    public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default) =>
        Task.FromResult<DeezerAlbumDetail?>(null);

    public Task<IReadOnlyList<DeezerGenre>> GetGenresAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeezerGenre>>(Genres);

    public Task<IReadOnlyList<DeezerPlaylistSummary>> GetChartPlaylistsAsync(long? genreId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeezerPlaylistSummary>>(ChartPlaylists);

    public Task<IReadOnlyList<DeezerPlaylistSummary>> SearchPlaylistsAsync(string query, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeezerPlaylistSummary>>(SearchResults);

    public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(Playlists.GetValueOrDefault(id));

    public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default)
    {
        IReadOnlyList<DeezerPlaylistTrack> tracks = PlaylistTracks.GetValueOrDefault(id) ?? [];
        if (maxTracks is { } cap && tracks.Count > cap)
            tracks = tracks.Take(cap).ToList();
        return Task.FromResult(new DeezerPlaylistTracksResult(tracks, IsComplete: !IncompletePlaylistTracks.Contains(id)));
    }
}

/// <summary>Configurable <see cref="MusicHoarder.Api.Spotify.ISpotifyIsrcResolver"/> — maps ISRC → Spotify track id.</summary>
public sealed class FakeSpotifyIsrcResolver : MusicHoarder.Api.Spotify.ISpotifyIsrcResolver
{
    public Dictionary<string, string> ByIsrc { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> ResolveTrackIdByIsrcAsync(string? isrc, CancellationToken ct = default) =>
        Task.FromResult(isrc is not null ? ByIsrc.GetValueOrDefault(isrc) : null);
}
