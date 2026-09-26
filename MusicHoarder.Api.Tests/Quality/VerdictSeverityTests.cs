using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class VerdictSeverityTests
{
    [Fact]
    public void WorstFirstRank_OrdersBySeverityNotEnumValue_UngradeableLast()
    {
        var ordered = Enum.GetValues<SongQualityVerdict>().OrderBy(VerdictSeverity.WorstFirstRank);

        Assert.Equal(
            [
                SongQualityVerdict.Wrong,
                SongQualityVerdict.Questionable,
                SongQualityVerdict.Good,
                SongQualityVerdict.Excellent,
                SongQualityVerdict.Ungradeable,
            ],
            ordered);
    }

    [Fact]
    public void WorstFirstRank_GivesEveryVerdictItsOwnRank()
    {
        var ranks = Enum.GetValues<SongQualityVerdict>().Select(VerdictSeverity.WorstFirstRank).ToList();

        Assert.Equal(ranks.Count, ranks.Distinct().Count());
    }

    [Theory]
    // A real verdict always beats "no judgement", however many Ungradeable grades sit beside it.
    [InlineData(SongQualityVerdict.Wrong, SongQualityVerdict.Ungradeable, SongQualityVerdict.Ungradeable, SongQualityVerdict.Wrong)]
    [InlineData(SongQualityVerdict.Ungradeable, SongQualityVerdict.Excellent, SongQualityVerdict.Good, SongQualityVerdict.Good)]
    [InlineData(SongQualityVerdict.Excellent, SongQualityVerdict.Questionable, SongQualityVerdict.Wrong, SongQualityVerdict.Wrong)]
    // Ungradeable only when nothing was judged.
    [InlineData(SongQualityVerdict.Ungradeable, SongQualityVerdict.Ungradeable, SongQualityVerdict.Ungradeable, SongQualityVerdict.Ungradeable)]
    public void WorstOf_PicksTheMostSevereJudgement(
        SongQualityVerdict a, SongQualityVerdict b, SongQualityVerdict c, SongQualityVerdict expected)
    {
        Assert.Equal(expected, VerdictSeverity.WorstOf([a, b, c]));
    }

    [Theory]
    [InlineData(SongQualityVerdict.Wrong, true)]
    [InlineData(SongQualityVerdict.Questionable, true)]
    [InlineData(SongQualityVerdict.Good, false)]
    [InlineData(SongQualityVerdict.Excellent, false)]
    [InlineData(SongQualityVerdict.Ungradeable, false)]
    public void IsAiFlagged_IsWrongOrQuestionable(SongQualityVerdict verdict, bool expected)
    {
        Assert.Equal(expected, VerdictSeverity.IsAiFlagged(verdict));
    }

    [Theory]
    [InlineData(SongQualityVerdict.Ungradeable, false)]
    [InlineData(SongQualityVerdict.Wrong, true)]
    [InlineData(SongQualityVerdict.Excellent, true)]
    public void IsJudgement_ExcludesOnlyUngradeable(SongQualityVerdict verdict, bool expected)
    {
        Assert.Equal(expected, VerdictSeverity.IsJudgement(verdict));
    }
}
