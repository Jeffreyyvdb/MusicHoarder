namespace MusicHoarder.Api.Spotify;

public interface ISpotifyLibraryComparisonService
{
    Task<SpotifyComparisonResponse> CompareAsync(int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<SpotifyComparisonSummaryResponse> GetSummaryAsync(CancellationToken ct = default);
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
    double? MatchConfidence);

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
