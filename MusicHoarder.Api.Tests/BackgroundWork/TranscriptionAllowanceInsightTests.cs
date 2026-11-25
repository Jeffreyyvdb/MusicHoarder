using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class TranscriptionAllowanceInsightTests
{
    private static void Spend(BackgroundWorkHarness h, TimeSpan ago, double seconds, TranscriptionPurpose purpose)
        => h.Seed(db => db.TranscriptionAudioSpends.Add(
            new TranscriptionAudioSpend { AtUtc = h.Now - ago, AudioSeconds = seconds, Purpose = purpose }));

    [Fact]
    public async Task What_is_left_of_the_day_counts_every_purpose_in_the_last_24_hours_only()
    {
        using var h = new BackgroundWorkHarness();
        h.Transcription.BaseUrl = GroqUrl;
        Spend(h, TimeSpan.FromMinutes(30), 2000, TranscriptionPurpose.AutoSync);
        Spend(h, TimeSpan.FromHours(5), 500, TranscriptionPurpose.TimingProbe);
        Spend(h, TimeSpan.FromHours(2), 1000, TranscriptionPurpose.ManualTranscribe);
        Spend(h, TimeSpan.FromDays(3), 9000, TranscriptionPurpose.AutoSync);

        var detail = await h.DescribeAsync(BackgroundJobIds.TranscriptionAllowance);

        Assert.Equal(16500, detail.Summary.Headline.Value);
        Assert.Equal(20000, detail.Summary.Headline.Of);
        Assert.Equal(BackgroundFormat.Duration, detail.Summary.Headline.Format);

        var figures = detail.Panel<FiguresPanelDto>("The allowance right now");
        Assert.Equal(2000, figures.Item("Spent in the last hour").Value);
        Assert.Equal(3500, figures.Item("Spent in the last 24 hours").Value);
        Assert.Equal(28800 - 3500, figures.Item("Kept for manual runs").Value);
        Assert.Equal(3, figures.Item("Requests in the last 24 hours").Value);

        var breakdown = detail.Panel<BreakdownPanelDto>("Where the last 24 hours went");
        Assert.Equal(3500, breakdown.Total);
        Assert.Equal(2000, breakdown.Segment("ai-sync"));
        Assert.Equal(500, breakdown.Segment("probe"));
        Assert.Equal(1000, breakdown.Segment("manual"));
    }

    private const string GroqUrl = "https://api.groq.com/openai/v1";

    [Fact]
    public async Task The_series_stack_each_purpose_in_clock_hours_and_draw_the_ceilings()
    {
        using var h = new BackgroundWorkHarness();
        h.Transcription.BaseUrl = GroqUrl;
        Spend(h, TimeSpan.FromMinutes(30), 2000, TranscriptionPurpose.AutoSync);
        Spend(h, TimeSpan.FromHours(2), 1000, TranscriptionPurpose.ManualTimingProbe);

        var detail = await h.DescribeAsync(BackgroundJobIds.TranscriptionAllowance);

        var hourly = detail.Panel<SeriesPanelDto>("Audio sent per hour");
        Assert.Equal("hour", hourly.Bucket);
        Assert.True(hourly.Stacked);
        Assert.Equal(48, hourly.Points.Count);
        Assert.Equal(["ai-sync", "resync", "probe", "manual"], hourly.Series.Select(s => s.Key));
        Assert.Equal([2000.0, 0, 0, 0], hourly.Points[^2].Values);
        Assert.Equal([0.0, 0, 0, 1000], hourly.Points[^3].Values);
        Assert.Equal([6000.0, 7200], hourly.Limits.Select(l => l.Value));
        Assert.Equal(["Background ceiling per hour", "Groq free tier per hour"], hourly.Limits.Select(l => l.Label));

        var daily = detail.Panel<SeriesPanelDto>("Audio sent per day");
        Assert.Equal(30, daily.Points.Count);
        Assert.Equal([20000.0, 28800, 6000], daily.Limits.Select(l => l.Value));
        Assert.Equal(3000, daily.Points[^1].Values.Sum());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://stt.local:9000/v1")]
    [InlineData("https://groq.com.example.test/v1")]
    [InlineData("not a url")]
    public async Task Groqs_free_tier_is_drawn_only_when_the_provider_is_groq(string baseUrl)
    {
        using var h = new BackgroundWorkHarness();
        h.Transcription.BaseUrl = baseUrl;
        Spend(h, TimeSpan.FromMinutes(30), 2000, TranscriptionPurpose.AutoSync);

        var detail = await h.DescribeAsync(BackgroundJobIds.TranscriptionAllowance);

        Assert.Equal([6000.0], detail.Panel<SeriesPanelDto>("Audio sent per hour").Limits.Select(l => l.Value));
        Assert.Equal([20000.0, 6000], detail.Panel<SeriesPanelDto>("Audio sent per day").Limits.Select(l => l.Value));
        Assert.DoesNotContain(
            detail.Panel<FiguresPanelDto>("The allowance right now").Items, i => i.Label == "Kept for manual runs");
    }

    [Fact]
    public async Task A_spent_hour_is_waiting_until_the_oldest_spend_ages_out()
    {
        using var h = new BackgroundWorkHarness();
        Spend(h, TimeSpan.FromMinutes(20), 6000, TranscriptionPurpose.AutoSync);

        var summary = await h.SummarizeAsync(BackgroundJobIds.TranscriptionAllowance);

        Assert.Equal(BackgroundJobState.Waiting, summary.State);
        Assert.Contains("hourly", summary.StateLabel);
        Assert.Equal(h.Now.AddMinutes(40), summary.NextRunAtUtc);
    }

    [Fact]
    public async Task Recent_background_spend_with_room_left_is_working_and_a_quiet_ledger_is_idle()
    {
        using var h = new BackgroundWorkHarness();
        Assert.Equal(BackgroundJobState.Idle, (await h.SummarizeAsync(BackgroundJobIds.TranscriptionAllowance)).State);

        Spend(h, TimeSpan.FromMinutes(3), 30, TranscriptionPurpose.TimingProbe);
        Assert.Equal(BackgroundJobState.Working, (await h.SummarizeAsync(BackgroundJobIds.TranscriptionAllowance)).State);
    }

    [Fact]
    public async Task Manual_spend_alone_does_not_make_the_sweeps_look_busy()
    {
        using var h = new BackgroundWorkHarness();
        Spend(h, TimeSpan.FromMinutes(3), 300, TranscriptionPurpose.ManualTranscribe);

        Assert.Equal(BackgroundJobState.Idle, (await h.SummarizeAsync(BackgroundJobIds.TranscriptionAllowance)).State);
    }

    [Fact]
    public async Task Without_a_provider_the_job_is_off()
    {
        using var h = new BackgroundWorkHarness();
        h.Transcriber.IsConfigured = false;

        var summary = await h.SummarizeAsync(BackgroundJobIds.TranscriptionAllowance);

        Assert.Equal(BackgroundJobState.Off, summary.State);
    }

    [Fact]
    public async Task The_trend_is_audio_per_utc_day_over_14_days()
    {
        using var h = new BackgroundWorkHarness();
        Spend(h, TimeSpan.FromMinutes(30), 100, TranscriptionPurpose.AutoSync);
        Spend(h, TimeSpan.FromDays(1), 50, TranscriptionPurpose.TimingProbe);

        var trend = (await h.SummarizeAsync(BackgroundJobIds.TranscriptionAllowance)).Trend;

        Assert.Equal(14, trend.Count);
        Assert.Equal(100, trend[^1]);
        Assert.Equal(50, trend[^2]);
    }
}
