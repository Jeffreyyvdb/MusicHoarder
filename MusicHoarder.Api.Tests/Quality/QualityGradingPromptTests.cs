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

    // --- replies that hold no grade (gpt-oss leaking its reasoning channels) ---
    //
    // These are stored replies from production. The old parser took the first object that parsed and
    // defaulted a missing score to 0, persisting each as an "ungradeable" verdict. A reply without a
    // grade must instead throw, so the grader records a retryable failure.

    [Fact]
    public void Parse_FinalChannelAsJsonString_ReadsTheGradeInside()
    {
        var result = QualityGradingPrompt.Parse(
            """
            {"analysis":"We need to grade the final chosen metadata ... Use 95. Provide issues empty array. Let's do. }","final":"{\"score\":95,\"verdict\":\"excellent\",\"summary\":\"The metadata is correct, corroborated by multiple providers, and the destination path matches the chosen metadata.\",\"issues\":[]}"}
            """);

        Assert.Equal(95, result.Score);
        Assert.Equal(SongQualityVerdict.Excellent, result.Verdict);
        Assert.Equal(
            "The metadata is correct, corroborated by multiple providers, and the destination path matches the chosen metadata.",
            result.Summary);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("""{"analysis":"We need to grade the final chosen metadata ... So just looks_correct. Score 95. Let's output. }" }""")]
    [InlineData("""{ }""")]
    [InlineData("""{ "analysis": [ ]}""")]
    [InlineData("""{"commentary to=assistant{" : "analysis"}""")]
    [InlineData("""{"analysis":"We need to grade ... But the instructions: " , "issues": [ { "code": "<snake_case>", "severity": "low" } ] }""")]
    public void Parse_ReplyWithoutAGrade_Throws(string reply)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => QualityGradingPrompt.Parse(reply));
        Assert.False(QualityGradingPrompt.TryParse(reply, out _));
    }

    [Fact]
    public void Parse_FinalChannelAsObject_ReadsItsGrade()
    {
        var result = QualityGradingPrompt.Parse(
            """{"analysis":"Nothing matched.","final":{"score":35,"verdict":"wrong","summary":"No provider matched."}}""");

        Assert.Equal(35, result.Score);
        Assert.Equal(SongQualityVerdict.Wrong, result.Verdict);
        Assert.Equal("No provider matched.", result.Summary);
    }

    [Fact]
    public void Parse_GradelessObjectFollowedByARealGrade_ReadsTheRealGrade()
    {
        var result = QualityGradingPrompt.Parse(
            """
            {"analysis":"Two providers agree; the album is missing. Score 72."}
            {"score": 72, "verdict": "good", "summary": "Correct but thinly sourced."}
            """);

        Assert.Equal(72, result.Score);
        Assert.Equal(SongQualityVerdict.Good, result.Verdict);
        Assert.Equal("Correct but thinly sourced.", result.Summary);
    }

    // A child of a gradeless object (an issue, a nested draft) is not the model's answer: reading it
    // would persist a verdict nobody gave, e.g. put a track in the Inbox's AI-flagged queue.
    [Theory]
    [InlineData("""{"verdict":"mostly good","summary":"fine","issues":[{"code":"x","verdict":"wrong"}]}""")]
    [InlineData("""{"score":"high","verdict":"n/a","summary":"s","issues":[{"code":"x","severity":"low","score":40}]}""")]
    public void Parse_GradeInsideAGradelessObject_IsNotTheGrade(string reply)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => QualityGradingPrompt.Parse(reply));
    }

    [Fact]
    public void Parse_LargeGradelessObject_DoesNotUseUpTheSearchBeforeTheRealGrade()
    {
        // 300 issue objects: rescanning each child would spend the whole candidate budget in here.
        var issues = string.Join(",", Enumerable.Range(0, 300).Select(i => $$"""{"code":"c{{i}}","severity":"low"}"""));
        var reply = $$"""
            {"analysis":"drafting","issues":[{{issues}}]}
            {"score": 72, "verdict": "good", "summary": "Correct but thinly sourced."}
            """;

        var result = QualityGradingPrompt.Parse(reply);

        Assert.Equal(SongQualityVerdict.Good, result.Verdict);
        Assert.Equal(72, result.Score);
    }

    [Fact]
    public void Parse_ExplicitUngradeableVerdict_IsStillAGrade()
    {
        var result = QualityGradingPrompt.Parse("""{"score":0,"verdict":"ungradeable"}""");

        Assert.Equal(0, result.Score);
        Assert.Equal(SongQualityVerdict.Ungradeable, result.Verdict);
        Assert.Null(result.Summary);
    }

    [Fact]
    public void Parse_VerdictMatchIgnoresCaseAndWhitespace()
    {
        Assert.Equal(SongQualityVerdict.Questionable, QualityGradingPrompt.Parse("""{"verdict":"  Questionable "}""").Verdict);
    }

    [Theory]
    [InlineData("""{"verdict":"meh"}""")]            // unrecognised verdict, no score
    [InlineData("""{"score":"95"}""")]               // a score that is not an integer
    [InlineData("""{"score":95.5,"verdict":7}""")]  // neither an integer score nor a verdict word
    public void Parse_NoIntegerScoreAndNoRecognisedVerdict_Throws(string reply)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => QualityGradingPrompt.Parse(reply));
    }

    [Theory]
    [InlineData("""{'score': 90, 'verdict': 'excellent'}""")]
    [InlineData("""{"issues": [ { "code": <snake_case>, "severity": "low" } ] }""")]
    [InlineData("""{"score": 90, \"verdict\": \"excellent\"}""")]
    [InlineData("""{"score": 90, <truncated""")]
    public async Task Parse_StrayCharacterOutsideAString_TerminatesAndThrows(string reply)
    {
        // A non-JSON character outside a string used to rewind the scanner onto itself forever,
        // spinning a grading worker. It must now fail the candidate and move on.
        var parse = Task.Run(() => QualityGradingPrompt.Parse(reply));

        Assert.Same(parse, await Task.WhenAny(parse, Task.Delay(TimeSpan.FromSeconds(5)))); // terminated
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => parse);
    }

    [Fact]
    public async Task Parse_StrayCharacterInProseBeforeTheGrade_StillFindsTheGrade()
    {
        var parse = Task.Run(() => QualityGradingPrompt.Parse(
            "Let me check { the artist's <name> } first.\n{\"score\": 81, \"verdict\": \"good\"}"));

        Assert.Same(parse, await Task.WhenAny(parse, Task.Delay(TimeSpan.FromSeconds(5)))); // terminated
        Assert.Equal(SongQualityVerdict.Good, (await parse).Verdict);
    }

    [Fact]
    public void TryParse_BlankReply_ReturnsFalse()
    {
        Assert.False(QualityGradingPrompt.TryParse(null, out _));
        Assert.False(QualityGradingPrompt.TryParse("  ", out _));
    }

    [Fact]
    public void TryParse_Grade_ReturnsIt()
    {
        Assert.True(QualityGradingPrompt.TryParse("""{"score": 18, "verdict": "wrong"}""", out var result));
        Assert.Equal(SongQualityVerdict.Wrong, result.Verdict);
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
