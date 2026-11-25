using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class QualityGradingInsightTests
{
    private static SongMetadata Song(BackgroundWorkHarness h, int n, Action<SongMetadata>? configure = null)
    {
        SongMetadata? song = null;
        h.Seed(db =>
        {
            song = BackgroundWorkTestData.NewSong(n, s => { s.EnrichedAtUtc = h.Now.AddDays(-5); configure?.Invoke(s); });
            db.Songs.Add(song);
        });
        return song!;
    }

    private static void Grade(
        BackgroundWorkHarness h, SongMetadata song, SongQualityVerdict verdict, int score, TimeSpan ago,
        string? summary = "ok", string? raw = null)
        => h.Seed(db => db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = song.Id, OwnerUserId = song.OwnerUserId, Verdict = verdict, Score = score,
            GradedAtUtc = h.Now - ago, Summary = summary, RawResponseJson = raw,
            PromptVersion = QualityGradingPrompt.Version, Model = h.Grading.Model,
        }));

    private static BackgroundWorkHarness Graded(out SongMetadata[] songs)
    {
        var h = new BackgroundWorkHarness();
        var made = Enumerable.Range(1, 6).Select(n => Song(h, n)).ToArray();
        songs = made;
        Grade(h, made[0], SongQualityVerdict.Excellent, 95, TimeSpan.FromDays(1));
        Grade(h, made[1], SongQualityVerdict.Wrong, 20, TimeSpan.FromDays(1));
        Grade(h, made[3], SongQualityVerdict.Good, 80, TimeSpan.FromDays(3));
        h.Seed(db => db.Songs.Single(s => s.Id == made[3].Id).EnrichedAtUtc = h.Now.AddDays(-1));
        Grade(h, made[4], SongQualityVerdict.Ungradeable, 0, TimeSpan.FromDays(1), summary: null, raw: "{}");
        Grade(h, made[5], SongQualityVerdict.Ungradeable, 0, TimeSpan.FromDays(1), summary: "no data");
        return h;
    }

    [Fact]
    public async Task Every_enriched_song_is_a_current_grade_or_waiting_for_one()
    {
        using var h = Graded(out _);
        Song(h, 20, s => s.IsDuplicate = true);
        Song(h, 21, s => s.EnrichmentStatus = EnrichmentStatus.Pending);

        var detail = await h.DescribeAsync(BackgroundJobIds.QualityGrading);

        var panel = detail.Panel<BreakdownPanelDto>("Latest verdict");
        Assert.Equal(6, panel.Total);
        Assert.Equal(1, panel.Segment("excellent"));
        Assert.Equal(1, panel.Segment("wrong"));
        Assert.Equal(1, panel.Segment("ungradeable"));
        Assert.Equal(3, panel.Segment("waiting"));
        Assert.Equal(3, detail.Summary.Headline.Value);
        Assert.Equal(6, detail.Summary.Headline.Of);
    }

    [Fact]
    public async Task The_eta_is_the_songs_waiting_over_the_grades_written_a_day()
    {
        using var h = Graded(out _);

        var headline = (await h.SummarizeAsync(BackgroundJobIds.QualityGrading)).Headline;

        Assert.Equal(h.Now.AddDays(3 / (5 / 3.0)), headline.EtaUtc);
    }

    [Fact]
    public async Task The_histogram_scores_current_grades_and_leaves_out_the_unjudged()
    {
        using var h = Graded(out _);

        var bars = (await h.DescribeAsync(BackgroundJobIds.QualityGrading)).Panel<BarsPanelDto>("Scores");

        Assert.Equal(10, bars.Bars.Count);
        Assert.Equal(1, bars.Bars[9].Value);
        Assert.Equal(1, bars.Bars[2].Value);
        Assert.Equal(2, bars.Bars.Sum(b => b.Value));
    }

    [Fact]
    public async Task Grades_from_an_old_prompt_are_counted_but_not_waiting()
    {
        using var h = Graded(out var songs);
        h.Seed(db => db.SongQualityGrades.Single(g => g.SongId == songs[0].Id).PromptVersion = QualityGradingPrompt.Version - 1);

        var detail = await h.DescribeAsync(BackgroundJobIds.QualityGrading);

        Assert.Equal(1, detail.Panel<FiguresPanelDto>("Around the grader").Item("Grades from an older prompt or model").Value);
        Assert.Equal(3, detail.Summary.Headline.Value);
    }

    [Fact]
    public async Task Grading_is_off_without_a_key_or_when_switched_off()
    {
        using var h = new BackgroundWorkHarness();
        Assert.NotEqual(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.QualityGrading)).State);

        h.Settings.Grading = false;
        Assert.Equal(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.QualityGrading)).State);

        h.Settings.Grading = true;
        h.Grading.ApiKey = "";
        Assert.Equal(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.QualityGrading)).State);
    }
}
