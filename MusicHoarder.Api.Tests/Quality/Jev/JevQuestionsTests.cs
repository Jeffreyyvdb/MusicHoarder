using System.Text;
using System.Text.Json;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Quality.Jev;

namespace MusicHoarder.Api.Tests.Quality.Jev;

public class JevQuestionsTests
{
    [Theory]
    [InlineData(0.0, 20)]
    [InlineData(1.0, 55)]
    [InlineData(1.5, 68)]   // halfway from 55 to 80 = 67.5, rounded away from zero
    [InlineData(2.0, 80)]
    [InlineData(2.5, 88)]   // 87.5
    [InlineData(3.0, 95)]
    [InlineData(-1.0, 20)]
    [InlineData(7.0, 95)]
    public void Score_position_maps_through_the_middle_of_each_band(double position, int expected)
    {
        Assert.Equal(expected, JevQuestions.ScoreFromPosition(position));
    }

    [Fact]
    public void Maps_song_answers_to_a_grade()
    {
        var answers = TrialTestKit.SongAnswers(verdict: "questionable", pWrong: 0.30, pQuestionable: 0.45, quality: 1.0, unsupported: 0.93).Answers
            .ToDictionary(a => a.Key, a => a.Value);
        answers["identity_changed"] = new DecisionAnswer("choice", Choice: "artist",
            Probabilities: new Dictionary<string, double> { ["none"] = 0.3, ["artist"] = 0.7 });

        var grade = JevQuestions.MapSong(answers);

        Assert.Equal(SongQualityVerdict.Questionable, grade.Verdict);
        Assert.Equal(55, grade.Score);
        Assert.Equal(0.75, grade.FlagProbability!.Value, 6);
        Assert.Equal(0.93, grade.UnsupportedIdentity);

        var unsupported = Assert.Single(grade.Issues, i => i.Code == "unsupported_identity");
        Assert.Equal("high", unsupported.Severity);
        var changed = Assert.Single(grade.Issues, i => i.Code == "artist_changed");
        Assert.Equal("medium", changed.Severity);
        Assert.DoesNotContain(grade.Issues, i => i.Code == "single_source"); // 0.1, under the threshold
        Assert.StartsWith("Questionable (45%); flags: unsupported identity (p=93%)", grade.Summary);
    }

    [Fact]
    public void Ungradeable_scores_zero()
    {
        var grade = JevQuestions.MapSong(TrialTestKit.SongAnswers(verdict: "ungradeable", quality: 3).Answers);
        Assert.Equal(SongQualityVerdict.Ungradeable, grade.Verdict);
        Assert.Equal(0, grade.Score);
    }

    [Fact]
    public void An_answer_set_without_a_verdict_is_an_error()
    {
        var answers = new Dictionary<string, DecisionAnswer> { ["quality"] = new("score", Score: 2) };
        Assert.Throws<JsonException>(() => JevQuestions.MapSong(answers));
    }

    [Fact]
    public void Every_verdict_option_is_a_verdict_the_mapping_reads()
    {
        foreach (var questions in new[] { JevQuestions.SongQuestions, JevQuestions.AlbumQuestions })
        foreach (var option in questions["verdict"].Options)
        {
            var answers = new Dictionary<string, DecisionAnswer>
            {
                ["verdict"] = new("choice", Choice: option.Key, Probabilities: new Dictionary<string, double> { [option.Key] = 1 }),
            };
            JevQuestions.MapSong(answers); // throws on an option it does not know
        }
    }

    [Fact]
    public void Noul_questions_name_both_outcomes()
    {
        foreach (var questions in new[] { JevQuestions.SongQuestions, JevQuestions.AlbumQuestions })
        foreach (var q in questions.Values.Where(q => q.Type == DecisionQuestionType.Noul))
            Assert.Equal(["true", "false"], q.Options.Select(o => o.Key).ToArray());
    }

    [Fact]
    public void A_small_dossier_is_sent_whole()
    {
        var state = JevQuestions.SongState(Dossier(changes: 3), maxBytes: 32_000);

        Assert.False(state.Shed);
        Assert.Equal(state.FullBytes, state.SentBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(state.State.ToJsonString()), state.SentBytes);
        Assert.NotNull(state.State["audit"]);
        Assert.Equal(3, state.State["dossier"]!["changeLog"]!.AsArray().Count);
    }

    [Fact]
    public void A_large_change_log_is_shed_newest_first_until_it_fits()
    {
        var dossier = Dossier(changes: 400);
        var state = JevQuestions.SongState(dossier, maxBytes: 32_000);

        Assert.True(state.Shed);
        Assert.True(state.FullBytes > 32_000);
        Assert.True(state.SentBytes <= 32_000);

        var kept = state.State["dossier"]!["changeLog"]!.AsArray();
        Assert.NotEmpty(kept);
        // The newest entry survives: it explains the current metadata.
        Assert.Equal("value 399", (string?)kept[^1]!["newValue"]);
        Assert.Equal(400, (int?)state.State["dossier"]!["truncation"]!["changeLogTotal"]);
    }

    [Fact]
    public void An_oversized_dossier_with_no_change_log_is_not_marked_shed()
    {
        var state = JevQuestions.SongState(Dossier(changes: 0), maxBytes: 500);

        Assert.False(state.Shed);
        Assert.True(state.SentBytes > 500);
        Assert.Null(state.State["dossier"]!["truncation"]);
    }

    internal static SongGradingDossier Dossier(int changes)
    {
        var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var meta = new DossierMetadata("Title", "Artist", "Artist", "Album", 2020, 1, null, null, null, null);
        return new SongGradingDossier(
            1,
            new DossierFile("/m/a.mp3", "a.mp3", ".mp3", 1000, 200, 320, true, now),
            meta, meta,
            new DossierEnrichment("Matched", "acoustid", 0.9, [], null, false, false),
            "/dest/a.mp3",
            [],
            Enumerable.Range(0, changes)
                .Select(i => new DossierChange("Title", $"old value {i} " + new string('x', 40), $"value {i}", "musicbrainz", 0.9,
                    Applied: true, Proposed: false, now.AddMinutes(i)))
                .ToList(),
            null);
    }
}
