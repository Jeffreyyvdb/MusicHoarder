using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class QualityRollupTests
{
    private static QualityRollup.GradeRow Row(SongQualityVerdict v, int score, string? issues = null)
        => new(v, score, issues);

    [Fact]
    public void Aggregate_Empty_ReturnsZeroedAggregate()
    {
        var agg = QualityRollup.Aggregate([]);

        Assert.Equal(0, agg.Graded);
        Assert.Null(agg.AverageScore);
        Assert.Empty(agg.TopIssues);
    }

    [Fact]
    public void Aggregate_CountsVerdicts()
    {
        var agg = QualityRollup.Aggregate(
        [
            Row(SongQualityVerdict.Excellent, 95),
            Row(SongQualityVerdict.Good, 80),
            Row(SongQualityVerdict.Good, 75),
            Row(SongQualityVerdict.Questionable, 50),
            Row(SongQualityVerdict.Wrong, 10),
            Row(SongQualityVerdict.Ungradeable, 0),
        ]);

        Assert.Equal(6, agg.Graded);
        Assert.Equal(1, agg.Verdicts.Excellent);
        Assert.Equal(2, agg.Verdicts.Good);
        Assert.Equal(1, agg.Verdicts.Questionable);
        Assert.Equal(1, agg.Verdicts.Wrong);
        Assert.Equal(1, agg.Verdicts.Ungradeable);
    }

    [Fact]
    public void Aggregate_AverageExcludesUngradeable()
    {
        var agg = QualityRollup.Aggregate(
        [
            Row(SongQualityVerdict.Good, 80),
            Row(SongQualityVerdict.Wrong, 20),
            Row(SongQualityVerdict.Ungradeable, 0), // must not drag the average down
        ]);

        Assert.Equal(50.0, agg.AverageScore);
    }

    [Fact]
    public void Aggregate_TopIssues_CountedAndOrderedByFrequency()
    {
        const string wrongId = """[{"code":"unsupported_identity","severity":"high"},{"code":"no_provider_match","severity":"high"}]""";
        const string lowConf = """[{"code":"low_confidence","severity":"medium"}]""";

        var agg = QualityRollup.Aggregate(
        [
            Row(SongQualityVerdict.Wrong, 10, wrongId),
            Row(SongQualityVerdict.Wrong, 12, wrongId),
            Row(SongQualityVerdict.Questionable, 55, lowConf),
        ]);

        // unsupported_identity + no_provider_match appear twice each; low_confidence once.
        Assert.Equal(2, agg.TopIssues.First(i => i.Code == "unsupported_identity").Count);
        Assert.Equal(2, agg.TopIssues.First(i => i.Code == "no_provider_match").Count);
        Assert.Equal(1, agg.TopIssues.First(i => i.Code == "low_confidence").Count);
        // Highest-frequency issues lead; equal counts tie-break alphabetically by code.
        Assert.Equal("no_provider_match", agg.TopIssues[0].Code);
    }

    [Fact]
    public void Aggregate_IgnoresMalformedIssueJson()
    {
        var agg = QualityRollup.Aggregate([Row(SongQualityVerdict.Wrong, 10, "not-json")]);

        Assert.Empty(agg.TopIssues);
        Assert.Equal(1, agg.Graded);
    }
}
