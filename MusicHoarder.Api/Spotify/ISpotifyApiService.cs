namespace MusicHoarder.Api.Spotify;

public interface ISpotifyApiService
{
    Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default);
    Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default);
}

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
    SpotifyLibraryMatchInfo? LibraryMatch = null);

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
