using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Snapshots;

/// <summary>Per-song state pulled from a snapshot, for diffing one version against another.</summary>
public readonly record struct SnapshotSongState(
    EnrichmentStatus Status, double? Confidence, string? MatchedBy, int? AiScore, SongQualityVerdict? AiVerdict);

public enum SnapshotChangeKind
{
    Regressed = -1,
    Unchanged = 0,
    Improved = 1,
}

/// <summary>
/// The rules that decide whether a song got better or worse between two snapshots. Kept out of the
/// endpoint so the classification is unit-testable. A regression in <em>any</em> dimension dominates
/// — a song that newly needs review is the headline even if its AI score nudged up.
/// </summary>
public static class SnapshotComparison
{
    /// <summary>AI score drop (points) that counts as a regression even when the status held.</summary>
    public const int AiScoreRegressionThreshold = 10;

    /// <summary>Quality order (higher = better) — deliberately distinct from the raw enum values.</summary>
    public static int StatusRank(EnrichmentStatus s) => s switch
    {
        EnrichmentStatus.Matched => 3,
        EnrichmentStatus.NeedsReview => 2,
        EnrichmentStatus.Pending => 1,
        EnrichmentStatus.Failed => 0,
        _ => 1,
    };

    public static (SnapshotChangeKind Kind, string[] Reasons) Classify(SnapshotSongState from, SnapshotSongState to)
    {
        var statusDelta = StatusRank(to.Status) - StatusRank(from.Status);
        var verdictDelta = (from.AiVerdict, to.AiVerdict) is ({ } fv, { } tv) ? (int)tv - (int)fv : 0;
        var scoreDelta = (from.AiScore, to.AiScore) is ({ } fs, { } ts) ? ts - fs : 0;

        var reasons = new List<string>();
        var regressed = false;
        var improved = false;

        if (statusDelta != 0)
        {
            reasons.Add($"{from.Status} → {to.Status}");
            if (statusDelta < 0) regressed = true; else improved = true;
        }
        if (verdictDelta != 0)
        {
            reasons.Add($"AI {from.AiVerdict} → {to.AiVerdict}");
            if (verdictDelta < 0) regressed = true; else improved = true;
        }
        if (Math.Abs(scoreDelta) >= AiScoreRegressionThreshold)
        {
            reasons.Add($"AI score {from.AiScore} → {to.AiScore}");
            if (scoreDelta < 0) regressed = true; else improved = true;
        }

        if (regressed) return (SnapshotChangeKind.Regressed, reasons.ToArray());
        if (improved) return (SnapshotChangeKind.Improved, reasons.ToArray());
        return (SnapshotChangeKind.Unchanged, []);
    }

    /// <summary>Signed magnitude of change; negative = worse. Used only for ordering the diff lists.</summary>
    public static int Severity(SnapshotSongState from, SnapshotSongState to)
    {
        var status = (StatusRank(to.Status) - StatusRank(from.Status)) * 100;
        var score = (to.AiScore ?? 0) - (from.AiScore ?? 0);
        var verdict = (from.AiVerdict, to.AiVerdict) is ({ } fv, { } tv) ? ((int)tv - (int)fv) * 25 : 0;
        return status + verdict + score;
    }
}
