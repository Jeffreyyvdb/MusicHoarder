using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class EnrichmentRetriesInsightTests
{
    private static BackgroundWorkHarness Loop()
    {
        var h = new BackgroundWorkHarness();
        var songs = new List<SongMetadata>();
        h.Seed(db =>
        {
            for (var n = 1; n <= 7; n++)
                songs.Add(BackgroundWorkTestData.NewSong(n, s => { if (s.FileName == "6.mp3") s.IsManuallyApproved = true; }));
            db.Songs.AddRange(songs);
            db.SaveChanges();

            SongProviderAttempt A(int song, EnrichmentProvider p, ProviderAttemptStatus st, DateTime? retryAfter = null, DateTime? next = null, DateTime? since = null)
                => new()
                {
                    SongId = songs[song - 1].Id, Provider = p, Status = st, AttemptedAtUtc = h.Now.AddMinutes(-30),
                    RetryAfterUtc = retryAfter, NextRetryAfterUtc = next, RateLimitedSinceUtc = since,
                };

            db.SongProviderAttempts.AddRange(
                A(1, EnrichmentProvider.AcoustID, ProviderAttemptStatus.RateLimited, h.Now.AddMinutes(-5), since: h.Now.AddDays(-2)),
                A(2, EnrichmentProvider.AcoustID, ProviderAttemptStatus.NoMatch, next: h.Now.AddHours(-1)),
                A(2, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Failed, next: h.Now.AddHours(-1)),
                A(3, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, next: h.Now.AddDays(2)),
                A(4, EnrichmentProvider.AcoustID, ProviderAttemptStatus.RateLimited, h.Now.AddMinutes(30), since: h.Now.AddHours(-1)),
                A(5, EnrichmentProvider.Tracker, ProviderAttemptStatus.RateLimited, h.Now.AddMinutes(-5), since: h.Now.AddDays(-9)),
                A(6, EnrichmentProvider.AcoustID, ProviderAttemptStatus.RateLimited, h.Now.AddMinutes(-5), since: h.Now.AddDays(-9)),
                A(7, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched));
        });
        return h;
    }

    [Fact]
    public async Task The_headline_counts_songs_with_a_due_retry_the_way_the_sweep_selects_them()
    {
        using var h = Loop();

        var summary = await h.SummarizeAsync(BackgroundJobIds.EnrichmentRetries);

        Assert.Equal(2, summary.Headline.Value);
    }

    [Fact]
    public async Task Stuck_rate_limits_are_surfaced_with_how_long_they_have_lasted()
    {
        using var h = Loop();

        var figures = (await h.DescribeAsync(BackgroundJobIds.EnrichmentRetries)).Panel<FiguresPanelDto>("Stuck loops");

        Assert.Equal(3, figures.Item("Retries due now").Value);
        Assert.Equal(2, figures.Item("Rate limited right now").Value);
        Assert.Equal(1, figures.Item("Rate limited for over a day").Value);
        Assert.Equal(BackgroundTone.Bad, figures.Item("Rate limited for over a day").Tone);
        Assert.Equal(h.Now.AddDays(-2), figures.Item("Longest rate limit began").AtUtc);
        Assert.Equal(1, figures.Item("Cooling down").Value);
    }

    [Fact]
    public async Task Waiting_attempts_are_counted_per_enabled_provider()
    {
        using var h = Loop();

        var bars = (await h.DescribeAsync(BackgroundJobIds.EnrichmentRetries)).Panel<BarsPanelDto>("Attempts waiting");

        Assert.Equal(["AcoustID", "Spotify"], bars.Bars.Select(b => b.Label));
        Assert.Equal([3.0, 2], bars.Bars.Select(b => b.Value));
    }

    [Fact]
    public async Task Attempts_land_in_the_hour_of_their_last_try_by_outcome()
    {
        using var h = Loop();

        var series = (await h.DescribeAsync(BackgroundJobIds.EnrichmentRetries)).Panel<SeriesPanelDto>("Provider attempts per hour");

        Assert.Equal(48, series.Points.Count);
        var hour = series.Points[^2].Values;
        Assert.Equal(8, hour.Sum());
        Assert.Equal(1, hour[0]);
    }

    [Fact]
    public async Task The_status_breakdown_counts_every_song()
    {
        using var h = Loop();

        var panel = (await h.DescribeAsync(BackgroundJobIds.EnrichmentRetries)).Panel<BreakdownPanelDto>("Every song");

        Assert.Equal(7, panel.Total);
        Assert.Equal(7, panel.Segment("Matched"));
    }

    [Fact]
    public async Task With_no_provider_enabled_the_job_is_off()
    {
        using var h = new BackgroundWorkHarness();
        h.Providers.Clear();

        Assert.Equal(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.EnrichmentRetries)).State);
    }
}
