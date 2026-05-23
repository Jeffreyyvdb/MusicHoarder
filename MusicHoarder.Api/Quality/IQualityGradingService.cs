using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public enum GradeOutcome
{
    /// <summary>A fresh grade was produced and persisted.</summary>
    Graded,
    /// <summary>The dossier, model and prompt version are unchanged since the last grade — reused it.</summary>
    Skipped,
    /// <summary>No API key/base URL configured — grading is off.</summary>
    NotConfigured,
    /// <summary>The song id doesn't exist (or isn't gradeable).</summary>
    NotFound,
    /// <summary>The grading call or parse failed.</summary>
    Failed,
}

public record GradeSongResult(GradeOutcome Outcome, SongQualityGrade? Grade, string? Error = null);

public interface IQualityGradingService
{
    /// <summary>
    /// Grades one song's enrichment result and persists a <see cref="SongQualityGrade"/>. Idempotent
    /// unless <paramref name="force"/> is set: an unchanged dossier (same model + prompt version)
    /// reuses the last grade instead of spending tokens.
    /// </summary>
    Task<GradeSongResult> GradeSongAsync(int songId, bool force = false, CancellationToken ct = default);
}
