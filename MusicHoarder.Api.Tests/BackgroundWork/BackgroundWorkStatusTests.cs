using MusicHoarder.Api.BackgroundWork;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class BackgroundWorkStatusTests
{
    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static readonly DateTime T0 = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_job_nobody_reported_on_is_unknown_not_guessed()
    {
        Assert.Null(new BackgroundWorkStatus(BackgroundWorkTestData.Clock()).Get(BackgroundJobIds.Lyrics));
    }

    [Fact]
    public void The_latest_report_of_a_service_wins_and_idle_keeps_when_it_next_runs()
    {
        var clock = new Clock(T0);
        var status = new BackgroundWorkStatus(clock);

        status.Working(BackgroundJobIds.CoverArt);
        Assert.Equal(BackgroundJobState.Working, status.Get(BackgroundJobIds.CoverArt)!.State);

        clock.Now = T0.AddMinutes(3);
        status.Idle(BackgroundJobIds.CoverArt, T0.AddHours(6), worked: true);

        var runtime = status.Get(BackgroundJobIds.CoverArt)!;
        Assert.Equal(BackgroundJobState.Idle, runtime.State);
        Assert.Equal(T0.AddHours(6), runtime.NextRunAtUtc);
        Assert.Equal(T0.AddMinutes(3), runtime.LastWorkedAtUtc);
    }

    [Fact]
    public void An_idle_pass_that_did_nothing_does_not_move_last_worked()
    {
        var clock = new Clock(T0);
        var status = new BackgroundWorkStatus(clock);
        status.Idle(BackgroundJobIds.CoverArt, null, worked: true);

        clock.Now = T0.AddHours(1);
        status.Idle(BackgroundJobIds.CoverArt, null);

        Assert.Equal(T0, status.Get(BackgroundJobIds.CoverArt)!.LastWorkedAtUtc);
    }

    [Fact]
    public void Waiting_names_why_and_until_when()
    {
        var status = new BackgroundWorkStatus(BackgroundWorkTestData.Clock(T0));

        status.Waiting(BackgroundJobIds.Lyrics, "AI sync waits for the audio allowance.", T0.AddHours(2));

        var runtime = status.Get(BackgroundJobIds.Lyrics)!;
        Assert.Equal(BackgroundJobState.Waiting, runtime.State);
        Assert.Equal("AI sync waits for the audio allowance.", runtime.Reason);
        Assert.Equal(T0.AddHours(2), runtime.UntilUtc);
    }

    [Fact]
    public void A_failure_reports_the_exception_type_never_its_message()
    {
        var status = new BackgroundWorkStatus(BackgroundWorkTestData.Clock(T0));

        status.Failing(BackgroundJobIds.CoverArt, new HttpRequestException("GET https://x/?key=secret failed"), T0.AddHours(1));

        var runtime = status.Get(BackgroundJobIds.CoverArt)!;
        Assert.Equal(BackgroundJobState.Failing, runtime.State);
        Assert.Equal(nameof(HttpRequestException), runtime.Reason);
        Assert.DoesNotContain("secret", runtime.Reason);
    }

    [Fact]
    public void Two_services_of_one_job_merge_by_what_matters_most()
    {
        var status = new BackgroundWorkStatus(BackgroundWorkTestData.Clock(T0));
        var job = BackgroundJobIds.AlbumFillAndFollows;

        status.Idle(job, T0.AddHours(1), source: "album-completion");
        status.Idle(job, T0.AddMinutes(20), source: "artist-monitor");
        Assert.Equal(T0.AddMinutes(20), status.Get(job)!.NextRunAtUtc);

        status.Working(job, source: "album-completion");
        Assert.Equal(BackgroundJobState.Working, status.Get(job)!.State);

        status.Failing(job, new InvalidOperationException(), null, source: "artist-monitor");
        Assert.Equal(BackgroundJobState.Failing, status.Get(job)!.State);
    }

    [Fact]
    public void A_long_reason_is_cut()
    {
        var status = new BackgroundWorkStatus(BackgroundWorkTestData.Clock(T0));
        status.Waiting(BackgroundJobIds.Lyrics, new string('x', 1000), null);

        Assert.Equal(200, status.Get(BackgroundJobIds.Lyrics)!.Reason!.Length);
    }
}
