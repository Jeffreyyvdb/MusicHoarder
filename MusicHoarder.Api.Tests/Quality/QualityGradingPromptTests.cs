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

    [Fact]
    public void Version_IsTwo()
    {
        // v2 added ground-truth (proposed != applied) and unreleased/community-tracker guidance.
        Assert.Equal(2, QualityGradingPrompt.Version);
    }

    [Fact]
    public void BuildMessages_SystemPrompt_TeachesProposedVsAppliedAndUnreleased()
    {
        // The two grader false positives this prompt fixes: treating a proposed-but-unapplied change
        // as applied, and grading an unreleased/community-tracker match "wrong" for lacking
        // mainstream corroboration. The system prompt must address both.
        var messages = QualityGradingPrompt.BuildMessages(SampleDossier());
        var system = messages.Single(m => m.Role == "system").Content;

        Assert.Contains("currentMetadata", system);
        Assert.Contains("proposed", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isUnreleased", system);
        Assert.Contains("community", system, StringComparison.OrdinalIgnoreCase);
    }

    private static SongGradingDossier SampleDossier()
    {
        var meta = new DossierMetadata("T", "A", "A", "Al", 2020, 1, null, null, null, null);
        return new SongGradingDossier(
            SongId: 1,
            File: new DossierFile("/x.mp3", "x.mp3", ".mp3", 1, 180, 320, true, DateTime.UtcNow),
            EmbeddedTags: meta,
            CurrentMetadata: meta,
            Enrichment: new DossierEnrichment("Matched", "Tracker", 1.0, [], null, false, true),
            DestinationPathPreview: "/dest/A/2020 - Al/01 - T.mp3",
            ProviderAttempts: [],
            ChangeLog: [],
            Duplicate: null);
    }
}
