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
    string? Artists = null,
    string? ArtistMusicBrainzIds = null,
    string? AlbumArtistMusicBrainzId = null,
    string? MusicBrainzReleaseGroupId = null,
    int? DiscNumber = null,
    int? TotalDiscs = null,
    int? TotalTracks = null,
    bool? IsCompilation = null,
    string? ReleaseTypePrimary = null,
    string? ReleaseTypes = null,
    /// <summary>Duration (seconds) of the recording this provider matched, for consensus identity
    /// comparison. Null when the provider doesn't report a duration.</summary>
    int? DurationSeconds = null,
    // --- Descriptive metadata (SpotiFLAC-inspired); see SongMetadata for field semantics. ---
    string? Genre = null,
    string? ReleaseDate = null,
    string? OriginalReleaseDate = null,
    string? Label = null,
    string? CatalogNumber = null,
    string? Upc = null,
    string? Composer = null,
    string? Copyright = null,
    string? ArtistSort = null,
    string? AlbumArtistSort = null);

public abstract record ProviderOutcome;
public sealed record ProviderMatched(EnrichmentProviderResult Result) : ProviderOutcome;
public sealed record ProviderNoMatch(EnrichmentProviderResult? BestCandidate = null) : ProviderOutcome;
public sealed record ProviderRateLimited(TimeSpan RetryAfter) : ProviderOutcome;

public interface IEnrichmentProvider
{
    /// <summary>
    /// The song columns any <see cref="CanHandle"/> reads. The startup sweeps call it on rows that
    /// carry only these (<see cref="SongMetadataLightQuery.WithOnly"/>), so an implementation that
    /// starts reading another property must add it here or it sees a blank there.
    /// </summary>
    static readonly SongColumnSet CanHandleColumns = new(
        nameof(SongMetadata.Artist),
        nameof(SongMetadata.Album),
        nameof(SongMetadata.Title),
        nameof(SongMetadata.TrackNumber),
        nameof(SongMetadata.SourcePath),
        nameof(SongMetadata.Fingerprint),
        nameof(SongMetadata.DurationSeconds),
        nameof(SongMetadata.MusicBrainzId),
        nameof(SongMetadata.Isrc));

    string Name { get; }
    int Priority { get; }
    bool CanHandle(SongMetadata song);
    Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default);
}
