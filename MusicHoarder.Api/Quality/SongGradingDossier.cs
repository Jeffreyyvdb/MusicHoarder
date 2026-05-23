namespace MusicHoarder.Api.Quality;

/// <summary>
/// Everything known about one song's journey through the pipeline: the file, its embedded tags,
/// what each provider returned, what the algorithm chose, and where it would be written. This is
/// the single payload fed to the grading LLM <em>and</em> emitted by the export endpoints, so the
/// grade and the human-debuggable dump always describe exactly the same inputs.
/// </summary>
public record SongGradingDossier(
    int SongId,
    DossierFile File,
    DossierMetadata EmbeddedTags,
    DossierMetadata CurrentMetadata,
    DossierEnrichment Enrichment,
    string? DestinationPathPreview,
    IReadOnlyList<DossierProviderAttempt> ProviderAttempts,
    IReadOnlyList<DossierChange> ChangeLog,
    DossierDuplicate? Duplicate);

public record DossierFile(
    string SourcePath,
    string FileName,
    string Extension,
    long FileSizeBytes,
    int? DurationSeconds,
    int? Bitrate,
    bool HasFingerprint,
    DateTime IndexedAtUtc);

public record DossierMetadata(
    string? Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    int? Year,
    int? TrackNumber,
    string? Artists,
    string? Isrc,
    string? MusicBrainzId,
    string? SpotifyId);

public record DossierEnrichment(
    string Status,
    string? MatchedBy,
    double? MatchConfidence,
    IReadOnlyList<string> Warnings,
    string? Error,
    bool IsManuallyApproved,
    bool IsUnreleased);

public record DossierProviderAttempt(
    string Provider,
    string Status,
    DateTime AttemptedAtUtc,
    string? Error,
    DossierCandidate? Candidate);

public record DossierCandidate(
    string? Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    int? Year,
    int? TrackNumber,
    string? Isrc,
    string? MusicBrainzId,
    string? SpotifyId,
    string? MatchedBy,
    double MatchConfidence,
    string RecommendedStatus,
    IReadOnlyList<string> Warnings);

public record DossierChange(
    string Field,
    string? OldValue,
    string? NewValue,
    string Source,
    double Confidence,
    bool Applied,
    bool Proposed,
    DateTime CreatedAtUtc);

public record DossierDuplicate(bool IsDuplicate, int? DuplicateOfId);
