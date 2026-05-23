using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class QualityGradingPromptTests
{
    [Fact]
    public void Parse_CleanJson_MapsAllFields()
    {
        var result = QualityGradingPrompt.Parse(
            """
            {"score": 92, "verdict": "excellent", "summary": "Corroborated by Spotify + MusicBrainz.",
             "issues": [{"code":"looks_correct","severity":"low","detail":"all good"}]}
            """);

        Assert.Equal(92, result.Score);
        Assert.Equal(SongQualityVerdict.Excellent, result.Verdict);
        Assert.Equal("Corroborated by Spotify + MusicBrainz.", result.Summary);
        Assert.Single(result.Issues);
        Assert.Equal("looks_correct", result.Issues[0].Code);
    }

    [Fact]
    public void Parse_StripsCodeFencesAndProse()
    {
        var result = QualityGradingPrompt.Parse(
            "Here is the grade:\n```json\n{\"score\": 15, \"verdict\": \"wrong\"}\n```\nHope that helps!");

        Assert.Equal(15, result.Score);
        Assert.Equal(SongQualityVerdict.Wrong, result.Verdict);
    }

    [Fact]
    public void Parse_MissingVerdict_BucketsByScore()
    {
        Assert.Equal(SongQualityVerdict.Excellent, QualityGradingPrompt.Parse("""{"score": 95}""").Verdict);
        Assert.Equal(SongQualityVerdict.Good, QualityGradingPrompt.Parse("""{"score": 75}""").Verdict);
        Assert.Equal(SongQualityVerdict.Questionable, QualityGradingPrompt.Parse("""{"score": 50}""").Verdict);
        Assert.Equal(SongQualityVerdict.Wrong, QualityGradingPrompt.Parse("""{"score": 20}""").Verdict);
        Assert.Equal(SongQualityVerdict.Ungradeable, QualityGradingPrompt.Parse("""{"score": 0}""").Verdict);
    }

    [Fact]
    public void Parse_ClampsScoreToRange()
    {
        Assert.Equal(100, QualityGradingPrompt.Parse("""{"score": 250, "verdict":"excellent"}""").Score);
        Assert.Equal(0, QualityGradingPrompt.Parse("""{"score": -5, "verdict":"ungradeable"}""").Score);
    }

    [Fact]
    public void Parse_IgnoresIssuesWithoutCode()
    {
        var result = QualityGradingPrompt.Parse(
            """{"score": 40, "verdict":"questionable", "issues":[{"severity":"low"},{"code":"low_confidence"}]}""");

        Assert.Single(result.Issues);
        Assert.Equal("low_confidence", result.Issues[0].Code);
    }
}
