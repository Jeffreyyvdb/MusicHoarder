using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class AlbumFillFollowsInsightTests
{
    private static void Item(
        BackgroundWorkHarness h, WishlistItemOrigin origin, WishlistItemStatus status,
        DateTime? next = null, TimeSpan? createdAgo = null, TimeSpan? updatedAgo = null)
        => h.Seed(db => db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = BackgroundWorkTestData.Owner, Origin = origin, Status = status, NextAttemptAtUtc = next,
            CreatedAtUtc = h.Now - (createdAgo ?? TimeSpan.FromDays(2)), UpdatedAtUtc = h.Now - (updatedAgo ?? TimeSpan.FromDays(2)),
        }));

    private static BackgroundWorkHarness Queue()
    {
        var h = new BackgroundWorkHarness();
        var fill = WishlistItemOrigin.AlbumCompletion;
        var follow = WishlistItemOrigin.ArtistMonitor;
        Item(h, fill, WishlistItemStatus.Pending);
        Item(h, fill, WishlistItemStatus.Downloading);
        Item(h, fill, WishlistItemStatus.Downloaded, updatedAgo: TimeSpan.FromHours(3));
        Item(h, fill, WishlistItemStatus.Downloaded, updatedAgo: TimeSpan.FromDays(1));
        Item(h, fill, WishlistItemStatus.Failed, next: h.Now.AddHours(2));
        Item(h, fill, WishlistItemStatus.Failed);
        Item(h, follow, WishlistItemStatus.Pending, createdAgo: TimeSpan.FromHours(1), updatedAgo: TimeSpan.FromHours(1));
        Item(h, follow, WishlistItemStatus.Downloaded, updatedAgo: TimeSpan.FromHours(1));
        Item(h, WishlistItemOrigin.UserRequested, WishlistItemStatus.Pending);
        return h;
    }

    [Fact]
    public async Task Waiting_tracks_are_pending_downloading_and_failed_with_a_retry_scheduled()
    {
        using var h = Queue();

        var summary = await h.SummarizeAsync(BackgroundJobIds.AlbumFillAndFollows);

        Assert.Equal(4, summary.Headline.Value);
        Assert.Equal(8, summary.Headline.Of);
    }

    [Fact]
    public async Task The_breakdown_crosses_origin_with_what_became_of_each_track()
    {
        using var h = Queue();

        var panel = (await h.DescribeAsync(BackgroundJobIds.AlbumFillAndFollows)).Panel<BreakdownPanelDto>("What became");

        Assert.Equal(8, panel.Total);
        Assert.Equal(2, panel.Segment("album-completion-pending"));
        Assert.Equal(1, panel.Segment("album-completion-retrying"));
        Assert.Equal(2, panel.Segment("album-completion-downloaded"));
        Assert.Equal(1, panel.Segment("album-completion-parked"));
        Assert.Equal(1, panel.Segment("artist-monitor-pending"));
        Assert.Equal(1, panel.Segment("artist-monitor-downloaded"));
    }

    [Fact]
    public async Task Queued_and_downloaded_tracks_are_charted_per_origin_per_day()
    {
        using var h = Queue();

        var detail = await h.DescribeAsync(BackgroundJobIds.AlbumFillAndFollows);

        var queued = detail.Panel<SeriesPanelDto>("Tracks queued per day");
        Assert.Equal(["album-completion", "artist-monitor"], queued.Series.Select(s => s.Key));
        Assert.Equal([0.0, 1], queued.Points[^1].Values);
        Assert.Equal([6.0, 1], queued.Points[^3].Values);

        var downloaded = detail.Panel<SeriesPanelDto>("Tracks downloaded per day");
        Assert.Equal([1.0, 1], downloaded.Points[^1].Values);
        Assert.Equal([1.0, 0], downloaded.Points[^2].Values);
        Assert.Equal(3, detail.Summary.Trend.Sum());
    }

    [Fact]
    public async Task The_eta_comes_from_what_has_been_downloaded_over_the_last_week()
    {
        using var h = Queue();

        var headline = (await h.SummarizeAsync(BackgroundJobIds.AlbumFillAndFollows)).Headline;

        Assert.Equal(h.Now.AddDays(4 / 1.5), headline.EtaUtc);
    }

    [Fact]
    public async Task Follows_report_their_checks_and_the_releases_they_found()
    {
        using var h = new BackgroundWorkHarness();
        h.Seed(db =>
        {
            var artist = new FollowedArtist
            {
                OwnerUserId = BackgroundWorkTestData.Owner, DeezerArtistId = "1", Name = "A",
                LastCheckedAtUtc = h.Now.AddHours(-3), NextCheckAtUtc = h.Now.AddHours(9),
            };
            db.FollowedArtists.AddRange(
                artist,
                new FollowedArtist
                {
                    OwnerUserId = BackgroundWorkTestData.Owner, DeezerArtistId = "2", Name = "B",
                    LastCheckedAtUtc = h.Now.AddHours(-9), NextCheckAtUtc = h.Now.AddHours(1), LastError = "x",
                });
            db.SaveChanges();
            db.FollowedArtistReleases.AddRange(
                new FollowedArtistRelease { FollowedArtistId = artist.Id, ReleaseKey = "a", Title = "New", Decision = FollowedReleaseDecision.Queued, DecidedAtUtc = h.Now.AddDays(-3) },
                new FollowedArtistRelease { FollowedArtistId = artist.Id, ReleaseKey = "b", Title = "Old", Decision = FollowedReleaseDecision.Baseline, DecidedAtUtc = h.Now.AddDays(-3) },
                new FollowedArtistRelease { FollowedArtistId = artist.Id, ReleaseKey = "c", Title = "Ancient", Decision = FollowedReleaseDecision.Queued, DecidedAtUtc = h.Now.AddDays(-60) });
        });

        var figures = (await h.DescribeAsync(BackgroundJobIds.AlbumFillAndFollows)).Panel<FiguresPanelDto>("Followed artists");

        Assert.Equal(2, figures.Item("Followed artists").Value);
        Assert.Equal(h.Now.AddHours(-3), figures.Item("Last check").AtUtc);
        Assert.Equal(h.Now.AddHours(1), figures.Item("Next check due").AtUtc);
        Assert.Equal(1, figures.Item("Checks that failed last time").Value);
        Assert.Equal(1, figures.Item("New releases found, last 30 days").Value);
    }

    [Fact]
    public async Task The_job_is_off_only_when_both_halves_are()
    {
        using var h = new BackgroundWorkHarness();
        Assert.NotEqual(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.AlbumFillAndFollows)).State);

        h.Enricher.EnableArtistMonitor = false;
        Assert.Equal(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.AlbumFillAndFollows)).State);

        h.Enricher.EnableWishlistDownloads = true;
        h.Settings.AlbumCompletion = true;
        Assert.NotEqual(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.AlbumFillAndFollows)).State);
    }
}
