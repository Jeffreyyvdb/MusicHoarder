using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Quality.Jev;

namespace MusicHoarder.Api.Tests.Quality.Jev;

/// <summary>
/// The trial only observes: a decision model's answer never becomes a subject's grade, and the
/// incumbent's grade is recorded for comparison without a second chat call.
/// </summary>
public class GraderTrialIsolationTests
{
    private const string Jev = "typesafe/jev-1.14-20261014";

    [Fact]
    public async Task A_decision_answer_never_becomes_the_current_grade()
    {
        var db = TrialTestKit.CreateDb();
        var song = TrialTestKit.AddSong(db);
        await db.SaveChangesAsync();
        var fingerprint = Fingerprint(song);
        var grade = TrialTestKit.Grade(song.Id, fingerprint, SongQualityVerdict.Good);
        db.SongQualityGrades.Add(grade);
        await db.SaveChangesAsync();

        var decisions = new TrialTestKit.FakeDecisionClient(_ => TrialTestKit.SongAnswers(verdict: "wrong", pWrong: 0.9, pQuestionable: 0.05));
        var result = await Jev_(db, decisions).GradeSongAsync(song.Id, Jev, fingerprint, GraderRunSource.Shadow);

        Assert.Equal(TrialRunStatus.Recorded, result.Status);
        var run = await db.GraderComparisonRuns.SingleAsync();
        Assert.Equal(GraderArm.Decision, run.Arm);
        Assert.Equal(SongQualityVerdict.Wrong, run.Verdict);
        Assert.Equal(fingerprint, run.InputFingerprint);
        Assert.Equal(0.0003m, run.CostUsd);
        Assert.False(run.StateShed);

        var current = await db.SongQualityGrades.LatestPerSong().SingleAsync();
        Assert.Equal(grade.Id, current.Id);
        Assert.Equal(SongQualityVerdict.Good, current.Verdict);
        Assert.Equal(1, await db.SongQualityGrades.CountAsync());
    }

    [Fact]
    public async Task A_changed_song_is_not_paired_with_its_old_grade()
    {
        var db = TrialTestKit.CreateDb();
        var song = TrialTestKit.AddSong(db);
        await db.SaveChangesAsync();

        var decisions = new TrialTestKit.FakeDecisionClient(_ => TrialTestKit.SongAnswers());
        var result = await Jev_(db, decisions).GradeSongAsync(song.Id, Jev, "STALE-FINGERPRINT", GraderRunSource.Shadow);

        Assert.Equal(TrialRunStatus.Stale, result.Status);
        Assert.Empty(decisions.Requests);
        Assert.False(await db.GraderComparisonRuns.AnyAsync());
    }

    [Fact]
    public async Task A_failed_decision_call_is_recorded_as_a_failure()
    {
        var db = TrialTestKit.CreateDb();
        var song = TrialTestKit.AddSong(db);
        await db.SaveChangesAsync();

        var decisions = new TrialTestKit.FakeDecisionClient(_ => throw new HttpRequestException("boom", null, System.Net.HttpStatusCode.BadGateway));
        var result = await Jev_(db, decisions).GradeSongAsync(song.Id, Jev, null, GraderRunSource.Manual);

        Assert.Equal("http_error", result.Run!.ErrorCode);
        Assert.Null(result.Run.Verdict);
        Assert.Equal(1, await db.GraderComparisonRuns.CountAsync());
    }

    [Fact]
    public async Task The_incumbent_grade_is_recorded_with_its_cost_while_the_trial_is_on()
    {
        var db = TrialTestKit.CreateDb();
        var song = TrialTestKit.AddSong(db);
        await db.SaveChangesAsync();
        var chat = new TrialTestKit.FakeChatClient("""{"score":82,"verdict":"good","summary":"ok","issues":[]}""");

        var result = await Incumbent(db, chat, trialEnabled: true).GradeSongAsync(song.Id);

        Assert.Equal(GradeOutcome.Graded, result.Outcome);
        Assert.Equal(1, chat.CallCount);
        var run = await db.GraderComparisonRuns.SingleAsync();
        Assert.Equal(GraderArm.Incumbent, run.Arm);
        Assert.Equal(GraderRunSource.Live, run.Source);
        Assert.Equal(result.Grade!.InputFingerprint, run.InputFingerprint);
        Assert.Equal(SongQualityVerdict.Good, run.Verdict);
        Assert.Equal(0.002m, run.CostUsd);
        Assert.Equal(1, run.Attempts);
        Assert.Equal(1500, run.OutputTokens);
        Assert.Equal(0, run.UnsupportedIdentity);
    }

    [Fact]
    public async Task Nothing_is_recorded_while_the_trial_is_off()
    {
        var db = TrialTestKit.CreateDb();
        var song = TrialTestKit.AddSong(db);
        await db.SaveChangesAsync();

        await Incumbent(db, new TrialTestKit.FakeChatClient("""{"score":82,"verdict":"good"}"""), trialEnabled: false).GradeSongAsync(song.Id);

        Assert.Equal(1, await db.SongQualityGrades.CountAsync());
        Assert.False(await db.GraderComparisonRuns.AnyAsync());
    }

    [Fact]
    public async Task The_shadow_pass_pairs_with_existing_grades_once()
    {
        var db = TrialTestKit.CreateDb();
        var a = TrialTestKit.AddSong(db, title: "A");
        var b = TrialTestKit.AddSong(db, title: "B");
        var demo = TrialTestKit.AddSong(db, owner: WellKnownUsers.DemoId, title: "Demo");
        await db.SaveChangesAsync();
        db.SongQualityGrades.AddRange(
            TrialTestKit.Grade(a.Id, Fingerprint(a)),
            TrialTestKit.Grade(b.Id, Fingerprint(b), SongQualityVerdict.Wrong),
            TrialTestKit.Grade(demo.Id, Fingerprint(demo), owner: WellKnownUsers.DemoId));
        await db.SaveChangesAsync();

        var decisions = new TrialTestKit.FakeDecisionClient(_ => TrialTestKit.SongAnswers());
        var runner = Runner(db, decisions, new JevGradingOptions { Concurrency = 1, RequestsPerSecond = 50 });

        var first = await runner.RunAsync(GraderSubjectKind.Song, 10, GraderRunSource.Shadow, default);
        var second = await runner.RunAsync(GraderSubjectKind.Song, 10, GraderRunSource.Shadow, default);

        Assert.Equal(2, first.Graded);       // the demo library is never graded
        Assert.Equal(2, first.Backfilled);   // both grades predate the trial
        Assert.Equal(0, second.Touched);
        Assert.Equal(2, decisions.Requests.Count);

        var runs = await db.GraderComparisonRuns.ToListAsync();
        Assert.Equal(2, runs.Count(r => r.Arm == GraderArm.Incumbent && r.Source == GraderRunSource.Backfill));
        Assert.Equal(2, runs.Count(r => r.Arm == GraderArm.Decision));
        Assert.Equal(SongQualityVerdict.Wrong, runs.Single(r => r.Arm == GraderArm.Incumbent && r.SubjectId == b.Id).Verdict);
        Assert.Equal(3, await db.SongQualityGrades.CountAsync());
    }

    [Fact]
    public async Task A_zero_sample_rate_grades_nothing()
    {
        var db = TrialTestKit.CreateDb();
        var a = TrialTestKit.AddSong(db);
        await db.SaveChangesAsync();
        db.SongQualityGrades.Add(TrialTestKit.Grade(a.Id, Fingerprint(a)));
        await db.SaveChangesAsync();

        var decisions = new TrialTestKit.FakeDecisionClient(_ => TrialTestKit.SongAnswers());
        var result = await Runner(db, decisions, new JevGradingOptions { ShadowSampleRate = 0, Concurrency = 1 })
            .RunAsync(GraderSubjectKind.Song, 10, GraderRunSource.Shadow, default);

        Assert.Equal(0, result.Touched);
        Assert.Empty(decisions.Requests);
    }

    private static readonly TrialTestKit.Monitor<QualityGradingOptions> Grading =
        new(new QualityGradingOptions { ApiKey = "sk-test", Model = "deepseek/deepseek-v4-flash" });

    private static QualityDossierFactory Factory() => new(new TrialTestKit.FixedResolver(), Grading);

    private static string Fingerprint(SongMetadata song) =>
        DossierFingerprint.Of(Factory().Build(song, []));

    private static JevGradingService Jev_(MusicHoarderDbContext db, IDecisionClient decisions, JevGradingOptions? opts = null) => new(
        new TrialTestKit.ScopeFactory(db), decisions, new TrialTestKit.FakeChatClient("{}"), Factory(),
        new TrialTestKit.NoAlbums(), new TrialTestKit.StubOwner(),
        new TrialTestKit.Monitor<JevGradingOptions>(opts ?? new JevGradingOptions()), Grading,
        NullLogger<JevGradingService>.Instance);

    private static GraderTrialRunner Runner(MusicHoarderDbContext db, IDecisionClient decisions, JevGradingOptions opts) => new(
        new TrialTestKit.ScopeFactory(db), Jev_(db, decisions, opts), new TrialTestKit.StubOwner(),
        new TrialTestKit.Monitor<JevGradingOptions>(opts), NullLogger<GraderTrialRunner>.Instance);

    private static QualityGradingService Incumbent(MusicHoarderDbContext db, IChatCompletionClient chat, bool trialEnabled) => new(
        new TrialTestKit.ScopeFactory(db), chat, Factory(), Grading,
        NullLogger<QualityGradingService>.Instance,
        new TrialTestKit.Monitor<JevGradingOptions>(new JevGradingOptions { Enabled = trialEnabled }));
}
