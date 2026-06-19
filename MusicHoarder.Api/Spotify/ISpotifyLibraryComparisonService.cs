namespace MusicHoarder.Api.Spotify;

public interface ISpotifyLibraryComparisonService
{
    Task<SpotifyComparisonResponse> CompareAsync(
        int offset = 0,
        int limit = 50,
        ComparisonMatchStatus? matchStatus = null,
        CancellationToken ct = default);

    Task<SpotifyComparisonSummaryResponse> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// Computes matches for the given tracks, persists to <see cref="Persistence.SpotifyTrackLibraryMatches"/>, returns Spotify id → info.
    /// </summary>
    Task<IReadOnlyDictionary<string, SpotifyLibraryMatchInfo>> UpsertMatchesForTracksAsync(
        IReadOnlyList<SpotifyTrackItem> tracks,
        string source,
        CancellationToken ct = default);

    /// <summary>
    /// Fills <see cref="SpotifyTrackItem.LibraryMatch"/> from DB cache, computing and persisting only for missing Spotify ids.
    /// </summary>
    Task<IReadOnlyList<SpotifyTrackItem>> AttachLibraryMatchesAsync(
        IReadOnlyList<SpotifyTrackItem> tracks,
        CancellationToken ct = default);

    /// <summary>
    /// Full scan of liked songs + persist matches and aggregate stats on <see cref="Persistence.SpotifySettings"/> (background sync).
    /// </summary>
    Task SyncLikedSongsMatchesAsync(CancellationToken ct = default);
}

public enum ComparisonMatchStatus
{
    InLibrary,
    PossibleMatch,
    NotInLibrary,
}

public record ComparisonMatchedTrack(
    int Id,
    string? Title,
    string? Artist,
    string EnrichmentStatus);

public record SpotifyComparisonItem(
    string SpotifyId,
    string Title,
    string Artist,
    string Album,
    string? AlbumArt,
    int DurationMs,
    DateTime AddedAt,
    ComparisonMatchStatus MatchStatus,
    ComparisonMatchedTrack? MatchedTrack,
    double? MatchConfidence,
    bool IsInWishlist = false);

public record SpotifyComparisonResponse(
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<SpotifyComparisonItem> Items);

public record SpotifyComparisonSummaryResponse(
    int Total,
    int InLibrary,
    int PossibleMatch,
    int NotInLibrary);
