using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public record GradeAlbumResult(GradeOutcome Outcome, CanonicalAlbumQualityGrade? Grade, string? Error = null, string? ErrorCode = null);

public interface IAlbumGradingService
{
    /// <summary>
    /// Grades whether a reconciled album (<see cref="CanonicalAlbum"/>) is the correct match for the
    /// owner's local files, persisting a <see cref="CanonicalAlbumQualityGrade"/>. Idempotent unless
    /// <paramref name="force"/>: an unchanged dossier (same model + prompt version) reuses the last grade.
    /// </summary>
    Task<GradeAlbumResult> GradeAlbumAsync(int canonicalAlbumId, bool force = false, CancellationToken ct = default);

    /// <summary>Builds the grading dossier for a fetched album (for the export endpoint); null if not found/fetched.</summary>
    Task<AlbumGradingDossier?> BuildDossierAsync(int canonicalAlbumId, CancellationToken ct = default);
}
