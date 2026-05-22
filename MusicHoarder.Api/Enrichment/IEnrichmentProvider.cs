using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public record EnrichmentProviderResult(
    string? Artist,
    string? AlbumArtist,
    string? Title,
    int? Year,
    int? TrackNumber,
    string? MusicBrainzId,
    string? MusicBrainzReleaseId,
    string? SpotifyId,
    string? AcoustIdTrackId,
    string? Isrc,
    string MatchedBy,
    double MatchConfidence,
    List<string> MatchWarnings,
    EnrichmentStatus RecommendedStatus,
    string? Album = null,
    int? DurationMs = null);

public abstract record ProviderOutcome;
public sealed record ProviderMatched(EnrichmentProviderResult Result) : ProviderOutcome;
public sealed record ProviderNoMatch(EnrichmentProviderResult? BestCandidate = null) : ProviderOutcome;
public sealed record ProviderRateLimited(TimeSpan RetryAfter) : ProviderOutcome;

public interface IEnrichmentProvider
{
    string Name { get; }
    int Priority { get; }
    bool CanHandle(SongMetadata song);
    Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default);
}
