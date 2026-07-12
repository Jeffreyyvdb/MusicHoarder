namespace MusicHoarder.Api.Spotify;

public interface ISpotifyApiService
{
    Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default);
    Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Fetches a single playlist's metadata by id (<c>GET /playlists/{id}</c>), independent of whether it
    /// is in the user's own playlist list. A 404 (<see cref="SpotifyPlaylistLookupResult.Blocked"/>) means
    /// Spotify blocks the playlist — editorial/algorithmic playlists 404 for personal API apps — or it
    /// doesn't exist.
    /// </summary>
    Task<SpotifyPlaylistLookupResult> GetPlaylistAsync(string playlistId, CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="ISpotifyApiService.GetPlaylistAsync"/>. <see cref="Found"/> carries the metadata;
/// <see cref="Blocked"/> distinguishes a 404 (editorial block / missing) from other failures.
/// </summary>
public record SpotifyPlaylistLookupResult(
    bool Found,
    SpotifyPlaylistItem? Playlist,
    bool Blocked,
    string? Message);

public record SpotifyLibraryMatchInfo(
    string MatchStatus,
    int? MatchedSongId,
    double? MatchConfidence,
    string? MatchedTitle,
    string? MatchedArtist,
    string? MatchedEnrichmentStatus);

public record SpotifyTrackItem(
    string SpotifyId,
    string Title,
    string Artist,
    string Album,
    string? AlbumArt,
    int DurationMs,
    DateTime AddedAt,
    string? Isrc = null,
    SpotifyLibraryMatchInfo? LibraryMatch = null,
    bool IsInWishlist = false);

public record SpotifyLikedSongsResponse(
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<SpotifyTrackItem> Items);

public record SpotifyPlaylistItem(
    string SpotifyId,
    string Name,
    string? Description,
    string? ImageUrl,
    int TrackCount,
    string? OwnerName);

public record SpotifyPlaylistsResponse(
    IReadOnlyList<SpotifyPlaylistItem> Items);

public record SpotifyPlaylistTracksResponse(
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<SpotifyTrackItem> Items);
