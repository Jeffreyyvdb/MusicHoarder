using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// The three "needs attention" buckets the AI-quality workbench is built around, derived purely
/// from a song's enrichment status <em>at grade time</em> and the LLM verdict.
/// </summary>
public enum QualityBucket
{
    /// <summary>Auto-accepted with a top-bucket grade, or anything else not worth surfacing.</summary>
    Other = 0,

    /// <summary>The algorithm wasn't confident and asked a human (NeedsReview at grade time).</summary>
    Flagged = 1,

    /// <summary>Auto-accepted (Matched), but the LLM grader rates it wrong/questionable — a silent failure.</summary>
    Silent = 2,

    /// <summary>Auto-accepted (Matched) and the LLM agrees it's excellent — verified clean.</summary>
    Verified = 3,
}

/// <summary>Pure classification of a latest grade into its workbench bucket. No EF, no IO.</summary>
public static class QualityBuckets
{
    public static QualityBucket Classify(string? enrichmentStatusAtGrade, SongQualityVerdict verdict)
    {
        if (string.Equals(enrichmentStatusAtGrade, "NeedsReview", StringComparison.OrdinalIgnoreCase))
            return QualityBucket.Flagged;

        if (string.Equals(enrichmentStatusAtGrade, "Matched", StringComparison.OrdinalIgnoreCase))
        {
            if (verdict is SongQualityVerdict.Wrong or SongQualityVerdict.Questionable)
                return QualityBucket.Silent;
            if (verdict == SongQualityVerdict.Excellent)
                return QualityBucket.Verified;
        }

        return QualityBucket.Other;
    }

    /// <summary>Lowercase wire name used by the API + frontend filter.</summary>
    public static string Name(this QualityBucket bucket) => bucket switch
    {
        QualityBucket.Flagged => "flagged",
        QualityBucket.Silent => "silent",
        QualityBucket.Verified => "verified",
        _ => "other",
    };
}
