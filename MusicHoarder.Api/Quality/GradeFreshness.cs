using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// The grade-staleness rule: a stored grade is "outdated" when the grading prompt version or the
/// configured model has changed since the grade was produced. Outdated grades are surfaced (never
/// auto-regraded) so the user can choose to refresh. This is the one place that pairs each grade
/// kind with its prompt-version constant — song grades age against
/// <see cref="QualityGradingPrompt.Version"/>, album grades against
/// <see cref="AlbumGradingPrompt.Version"/> — so a call site cannot mix the two up.
/// </summary>
public static class GradeFreshness
{
    public static bool IsSongGradeOutdated(int promptVersion, string? model, string currentModel) =>
        promptVersion != QualityGradingPrompt.Version
        || !string.Equals(model, currentModel, StringComparison.Ordinal);

    public static bool IsAlbumGradeOutdated(int promptVersion, string? model, string currentModel) =>
        promptVersion != AlbumGradingPrompt.Version
        || !string.Equals(model, currentModel, StringComparison.Ordinal);

    /// <summary>
    /// Whether the auto-grade sweep should grade a song again: never graded, re-enriched since its grade, or
    /// its grade is a blank. A prompt or model change is deliberately not a reason (see
    /// <see cref="IsSongGradeOutdated"/>).
    /// </summary>
    public static bool NeedsGrading(DateTime? gradedAtUtc, DateTime? enrichedAtUtc, bool isBlank)
        => gradedAtUtc is not { } graded
            || (enrichedAtUtc is { } enriched && graded < enriched)
            || isBlank;

    /// <summary>Query form of <see cref="IsSongGradeOutdated"/>, kept as a translatable expression.</summary>
    public static IQueryable<SongQualityGrade> WhereOutdated(this IQueryable<SongQualityGrade> grades, string currentModel) =>
        grades.Where(g => g.PromptVersion != QualityGradingPrompt.Version || g.Model != currentModel);

    /// <summary>Query form of <see cref="IsAlbumGradeOutdated"/>, kept as a translatable expression.</summary>
    public static IQueryable<CanonicalAlbumQualityGrade> WhereOutdated(this IQueryable<CanonicalAlbumQualityGrade> grades, string currentModel) =>
        grades.Where(g => g.PromptVersion != AlbumGradingPrompt.Version || g.Model != currentModel);
}

public static class QualityGradingQueries
{
    private static readonly EnrichmentStatus[] GradeableStatuses =
        [EnrichmentStatus.Matched, EnrichmentStatus.NeedsReview];

    /// <summary>Songs the auto-grade sweep may grade. The demo library is read-only and never graded.</summary>
    public static IQueryable<SongMetadata> WhereGradeable(this IQueryable<SongMetadata> songs)
        => songs
            .ExcludingDemoTenant()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .Where(s => GradeableStatuses.Contains(s.EnrichmentStatus));

    /// <summary>Albums the grader grades: those whose tracklist a catalog has supplied.</summary>
    public static IQueryable<CanonicalAlbum> WhereGradeable(this IQueryable<CanonicalAlbum> albums)
        => albums.Where(a => a.Status == CanonicalAlbumStatus.Fetched);
}
