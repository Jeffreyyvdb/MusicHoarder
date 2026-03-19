using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public record EnrichmentProviderResult(
    string? Artist,
    string? AlbumArtist,
    string? Title,
    int? Year,
    int? TrackNumber,
    string? MusicBrainzId,
    string? SpotifyId,
    string? Isrc,
    string MatchedBy,
    double MatchConfidence,
    List<string> MatchWarnings,
    EnrichmentStatus RecommendedStatus);

public interface IEnrichmentProvider
{
    string Name { get; }
    int Priority { get; }
    bool CanHandle(SongMetadata song);
    Task<EnrichmentProviderResult?> TryEnrichAsync(SongMetadata song, CancellationToken ct = default);
}
