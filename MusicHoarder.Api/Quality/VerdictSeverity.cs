using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// The one place that ranks <see cref="SongQualityVerdict"/>s by how badly they need a human. The
/// enum's numbers are a persisted contract and put <see cref="SongQualityVerdict.Ungradeable"/> at 0,
/// below <see cref="SongQualityVerdict.Wrong"/>, so ordering, <c>Min()</c>-ing or subtracting by
/// <c>(int)verdict</c> reads "the grader said nothing" as the worst possible grade — which once filled
/// every worst-offender slot with Ungradeable rows and emptied the Inbox's AI-flagged queue. Never
/// compare verdicts by their number; go through here. Song and album grades share the scale.
/// </summary>
public static class VerdictSeverity
{
    /// <summary>
    /// Sort key, worst first: Wrong, Questionable, Good, Excellent, then Ungradeable last — it is no
    /// judgement at all (the averages already exclude it), so it never outranks a real verdict.
    /// Among the judged verdicts the rank rises with quality.
    /// </summary>
    public static int WorstFirstRank(SongQualityVerdict verdict) => verdict switch
    {
        SongQualityVerdict.Wrong => 0,
        SongQualityVerdict.Questionable => 1,
        SongQualityVerdict.Good => 2,
        SongQualityVerdict.Excellent => 3,
        _ => 4, // Ungradeable
    };

    /// <summary>The worst verdict of a non-empty set; Ungradeable only when none of them is a judgement.</summary>
    public static SongQualityVerdict WorstOf(IEnumerable<SongQualityVerdict> verdicts) =>
        verdicts.MinBy(WorstFirstRank);

    /// <summary>Wrong or Questionable: the grade asks a human to look (the Inbox's "AI flagged" queue).</summary>
    public static bool IsAiFlagged(SongQualityVerdict verdict) =>
        verdict is SongQualityVerdict.Wrong or SongQualityVerdict.Questionable;

    /// <summary>
    /// Whether the grader judged the metadata at all. An Ungradeable grade (and its score, which means
    /// "no info") takes no part in better/worse comparisons.
    /// </summary>
    public static bool IsJudgement(SongQualityVerdict verdict) => verdict != SongQualityVerdict.Ungradeable;
}
