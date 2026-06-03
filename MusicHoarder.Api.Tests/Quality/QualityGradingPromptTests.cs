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
    public void Parse_TruncatedMidIssues_SalvagesCompletedFields()
    {
        // The exact production failure: the reply is cut off partway through the issues array.
        // We keep score/verdict/summary and every field that completed (the second issue's `code`
        // had finished, so it survives with a defaulted severity), and must NOT throw.
        var result = QualityGradingPrompt.Parse(
            """
            {
              "score": 35,
              "verdict": "wrong",
              "summary": "No provider matched yet a specific identity was chosen.",
              "issues": [
                {"code": "unsupported_identity", "severity": "high", "detail": "all good"},
                {"code": "no_provider_match", "severity": "med
            """);

        Assert.Equal(35, result.Score);
        Assert.Equal(SongQualityVerdict.Wrong, result.Verdict);
        Assert.Equal("No provider matched yet a specific identity was chosen.", result.Summary);
        Assert.Equal(2, result.Issues.Count);
        Assert.Equal("unsupported_identity", result.Issues[0].Code);
        Assert.Equal("no_provider_match", result.Issues[1].Code);
        Assert.Equal("medium", result.Issues[1].Severity); // truncated before its own severity → default
    }

    [Fact]
    public void Parse_TruncatedBeforeIssueCode_DropsIncompleteIssue()
    {
        // Cut off before the second issue's `code` completes → that issue has no code and is dropped.
        var result = QualityGradingPrompt.Parse(
            """{"score": 35, "verdict": "wrong", "issues": [{"code": "unsupported_identity"}, {"sever""");

        Assert.Equal(35, result.Score);
        Assert.Single(result.Issues);
        Assert.Equal("unsupported_identity", result.Issues[0].Code);
    }

    [Fact]
    public void Parse_TruncatedMidValue_SalvagesEarlierFields()
    {
        // Cut off mid-string before the verdict even completes — score still survives.
        var result = QualityGradingPrompt.Parse("""{"score": 88, "verdict": "go""");

        Assert.Equal(88, result.Score);
    }

    [Fact]
    public void Parse_TrailingProseAfterNestedObject_ParsesFirstBalancedObject()
    {
        // The wrong-last-brace case the old LastIndexOf('}') extractor failed on.
        var result = QualityGradingPrompt.Parse(
            """{"score": 72, "verdict": "good", "issues": [{"code":"single_source","severity":"low"}]} — hope that helps!""");

        Assert.Equal(72, result.Score);
        Assert.Equal(SongQualityVerdict.Good, result.Verdict);
        Assert.Single(result.Issues);
        Assert.Equal("single_source", result.Issues[0].Code);
    }

    [Fact]
    public void Parse_BraceInsideStringValue_IsNotConfused()
    {
        var result = QualityGradingPrompt.Parse(
            """{"score": 50, "verdict": "questionable", "summary": "path has a } and a { in it"}""");

        Assert.Equal(50, result.Score);
        Assert.Equal("path has a } and a { in it", result.Summary);
    }

    [Fact]
    public void Parse_ReasoningStyleProseWrappingObject_ExtractsIt()
    {
        // Mirrors the reasoning-model fallback: prose around a complete object.
        var result = QualityGradingPrompt.Parse(
            "Let me think about whether { this } is right.\nFinal answer:\n{\"score\": 95, \"verdict\": \"excellent\"}");

        Assert.Equal(95, result.Score);
        Assert.Equal(SongQualityVerdict.Excellent, result.Verdict);
    }

    [Fact]
    public void Parse_UnsalvageableGarbage_Throws()
    {
        // No JSON object at all → JsonException, which the service records as a clean "bad_response".
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => QualityGradingPrompt.Parse("I cannot grade this song."));
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
