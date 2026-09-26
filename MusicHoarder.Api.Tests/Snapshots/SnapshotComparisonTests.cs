using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.Tests.Snapshots;

public class SnapshotComparisonTests
{
    private static SnapshotSongState State(
        EnrichmentStatus status, double? conf = null, int? aiScore = null, SongQualityVerdict? verdict = null) =>
        new(status, conf, null, aiScore, verdict);

    [Fact]
    public void StatusRank_OrdersByQualityNotEnumValue()
    {
        Assert.True(SnapshotComparison.StatusRank(EnrichmentStatus.Matched)
            > SnapshotComparison.StatusRank(EnrichmentStatus.NeedsReview));
        Assert.True(SnapshotComparison.StatusRank(EnrichmentStatus.NeedsReview)
            > SnapshotComparison.StatusRank(EnrichmentStatus.Pending));
        Assert.True(SnapshotComparison.StatusRank(EnrichmentStatus.Pending)
            > SnapshotComparison.StatusRank(EnrichmentStatus.Failed));
    }

    [Fact]
    public void Classify_StatusDrop_IsRegression()
    {
        var (kind, reasons) = SnapshotComparison.Classify(
            State(EnrichmentStatus.Matched, conf: 0.92),
            State(EnrichmentStatus.NeedsReview, conf: 0.41));

        Assert.Equal(SnapshotChangeKind.Regressed, kind);
        Assert.Contains(reasons, r => r.Contains("Matched") && r.Contains("NeedsReview"));
    }

    [Fact]
    public void Classify_AiScoreDropBeyondThreshold_IsRegression()
    {
        var (kind, _) = SnapshotComparison.Classify(
            State(EnrichmentStatus.Matched, aiScore: 88, verdict: SongQualityVerdict.Good),
            State(EnrichmentStatus.Matched, aiScore: 52, verdict: SongQualityVerdict.Good));

        Assert.Equal(SnapshotChangeKind.Regressed, kind);
    }

    [Fact]
    public void Classify_SmallAiScoreWobble_IsUnchanged()
    {
        var (kind, _) = SnapshotComparison.Classify(
            State(EnrichmentStatus.Matched, aiScore: 88),
            State(EnrichmentStatus.Matched, aiScore: 85)); // -3, under the threshold

        Assert.Equal(SnapshotChangeKind.Unchanged, kind);
    }

    [Fact]
    public void Classify_StatusImprovement_IsImprovement()
    {
        var (kind, _) = SnapshotComparison.Classify(
            State(EnrichmentStatus.NeedsReview),
            State(EnrichmentStatus.Matched));

        Assert.Equal(SnapshotChangeKind.Improved, kind);
    }

    [Fact]
    public void Classify_VerdictDrop_IsRegression()
    {
        var (kind, _) = SnapshotComparison.Classify(
            State(EnrichmentStatus.Matched, verdict: SongQualityVerdict.Good),
            State(EnrichmentStatus.Matched, verdict: SongQualityVerdict.Wrong));

        Assert.Equal(SnapshotChangeKind.Regressed, kind);
    }

    [Fact]
    public void Classify_VerdictClimb_IsImprovement()
    {
        var (kind, _) = SnapshotComparison.Classify(
            State(EnrichmentStatus.Matched, verdict: SongQualityVerdict.Wrong),
            State(EnrichmentStatus.Matched, verdict: SongQualityVerdict.Good));

        Assert.Equal(SnapshotChangeKind.Improved, kind);
    }

    [Theory]
    // Ungradeable is 0 in the enum, so subtracting the numbers read Ungradeable → Wrong as a climb and
    // Excellent → Ungradeable as a four-step fall. It is no judgement either way.
    [InlineData(SongQualityVerdict.Ungradeable, 0, SongQualityVerdict.Wrong, 20)]
    [InlineData(SongQualityVerdict.Excellent, 95, SongQualityVerdict.Ungradeable, 0)]
    [InlineData(SongQualityVerdict.Ungradeable, 0, SongQualityVerdict.Excellent, 95)]
    public void Classify_ToOrFromUngradeable_IsUnchanged(
        SongQualityVerdict fromVerdict, int fromScore, SongQualityVerdict toVerdict, int toScore)
    {
        var (kind, reasons) = SnapshotComparison.Classify(
            State(EnrichmentStatus.Matched, aiScore: fromScore, verdict: fromVerdict),
            State(EnrichmentStatus.Matched, aiScore: toScore, verdict: toVerdict));

        Assert.Equal(SnapshotChangeKind.Unchanged, kind);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Classify_RegressionDominatesAConcurrentImprovement()
    {
        // Status improved, but the AI score cratered — the regression is the headline.
        var (kind, _) = SnapshotComparison.Classify(
            State(EnrichmentStatus.NeedsReview, aiScore: 90),
            State(EnrichmentStatus.Matched, aiScore: 50));

        Assert.Equal(SnapshotChangeKind.Regressed, kind);
    }
}
