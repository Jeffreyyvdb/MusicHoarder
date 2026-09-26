using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Recognises a stored grade that carries no judgement: the model's reply held no grade, yet it was
/// persisted as <see cref="SongQualityVerdict.Ungradeable"/> with score 0. That is what the parser
/// used to make of a reasoning model that leaked only its <c>analysis</c> channel, or replied
/// <c>{ }</c>, before <see cref="QualityGradingPrompt.Parse"/> learned to reject a reply without a
/// grade. Such a row counts as "never graded" — the grader does not reuse it and the auto-sweep
/// re-enqueues it — so the backlog heals on its own and no grade history is deleted. Song and album
/// grades share the reply schema and the verdict scale, so one rule serves both.
/// </summary>
public static class BlankGrade
{
    /// <summary>The longest raw reply kept on a grade row; a longer one is cut to this length.</summary>
    public const int MaxStoredResponseChars = 8192;

    public static bool IsBlank(this SongQualityGrade grade) =>
        IsBlank(grade.Verdict, grade.Summary, grade.RawResponseJson);

    public static bool IsBlank(this CanonicalAlbumQualityGrade grade) =>
        IsBlank(grade.Verdict, grade.Summary, grade.RawResponseJson);

    /// <summary>
    /// True when an <see cref="SongQualityVerdict.Ungradeable"/> grade without a summary came from a
    /// reply that, read by today's parser, is not a genuine "ungradeable" verdict: an empty reply, one
    /// with no grade in it, or one whose grade the old parser missed (it sat under <c>final</c>).
    /// </summary>
    /// <remarks>
    /// The stored reply is re-read rather than the row trusted, and the comparison cuts both ways. A
    /// legacy row whose reply did hold a grade re-reads as that grade, so it is flagged and regraded
    /// into its real verdict. A genuine summary-less <c>{"verdict": "ungradeable"}</c> re-reads as
    /// Ungradeable, so it is NOT flagged — and since every row today's grader writes re-reads as its
    /// own verdict, the sweep can never regrade the same subject in a loop. A reply cut at
    /// <see cref="MaxStoredResponseChars"/> can't be re-read faithfully (the grade may lie past the
    /// cut), so it is trusted as stored, erring towards the same guarantee.
    /// </remarks>
    public static bool IsBlank(SongQualityVerdict verdict, string? summary, string? rawResponse) =>
        verdict == SongQualityVerdict.Ungradeable
        && string.IsNullOrEmpty(summary) // exactly what WhereMaybeBlank can match in SQL
        && (string.IsNullOrWhiteSpace(rawResponse)
            || (rawResponse.Length < MaxStoredResponseChars
                && (!QualityGradingPrompt.TryParse(rawResponse, out var reread)
                    || reread.Verdict != SongQualityVerdict.Ungradeable)));

    /// <summary>
    /// Translatable pre-filter for <see cref="IsBlank(SongQualityGrade)"/>: the verdict and summary half
    /// of the rule, matching it exactly (so the sweep's blank page can't miss a row the rule would
    /// flag). Rows it keeps still need the in-memory check, which re-reads the stored reply.
    /// </summary>
    public static IQueryable<SongQualityGrade> WhereMaybeBlank(this IQueryable<SongQualityGrade> grades) =>
        grades.Where(g => g.Verdict == SongQualityVerdict.Ungradeable && (g.Summary == null || g.Summary == ""));

    /// <summary>Album form of <see cref="WhereMaybeBlank(IQueryable{SongQualityGrade})"/>.</summary>
    public static IQueryable<CanonicalAlbumQualityGrade> WhereMaybeBlank(this IQueryable<CanonicalAlbumQualityGrade> grades) =>
        grades.Where(g => g.Verdict == SongQualityVerdict.Ungradeable && (g.Summary == null || g.Summary == ""));
}
