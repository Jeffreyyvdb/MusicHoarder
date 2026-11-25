using MusicHoarder.Api.Auth;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class CoverArtInsightTests
{
    private static int _next;

    private static void Folder(BackgroundWorkHarness h, string folder, bool cover, Action<SongMetadata>? configure = null, int tracks = 1)
    {
        for (var i = 0; i < tracks; i++)
        {
            var n = Interlocked.Increment(ref _next);
            h.Seed(db => db.Songs.Add(BackgroundWorkTestData.NewSong(n, s =>
            {
                s.LibraryBuildStatus = LibraryBuildStatus.Done;
                s.DestinationPath = $"{folder}/{n}.mp3";
                s.HasCoverArt = cover && i == 0;
                configure?.Invoke(s);
            })));
        }
    }

    private static void CaaCover(BackgroundWorkHarness h, string folder)
        => h.Seed(db => db.LibraryWriteEvents.Add(new LibraryWriteEvent
        {
            OwnerUserId = BackgroundWorkTestData.Owner, Kind = LibraryWriteEventKind.AlbumCoverWritten,
            WrittenAtUtc = h.Now.AddHours(-1), AlbumFolder = folder, NewValue = "fetched:coverartarchive",
        }));

    private static void Attempt(BackgroundWorkHarness h, string folder, AlbumCoverFetchStatus status, DateTime? retry)
        => h.Seed(db => db.AlbumCoverFetchAttempts.Add(new AlbumCoverFetchAttempt
        {
            AlbumFolder = folder, Status = status, AttemptCount = 1, LastAttemptAtUtc = h.Now.AddHours(-2), NextRetryAfterUtc = retry,
        }));

    private static BackgroundWorkHarness Library()
    {
        var h = new BackgroundWorkHarness();
        Folder(h, "/dest/A", cover: true, tracks: 2);
        Folder(h, "/dest/B", cover: true);
        CaaCover(h, "/dest/B");
        Folder(h, "/dest/C", cover: true);
        CaaCover(h, "/dest/C");
        Attempt(h, "/dest/C", AlbumCoverFetchStatus.UpgradeNotFound, h.Now.AddDays(5));
        Folder(h, "/dest/D", cover: false);
        Folder(h, "/dest/E", cover: false);
        Attempt(h, "/dest/E", AlbumCoverFetchStatus.NotFound, h.Now.AddDays(3));
        Folder(h, "/dest/F", cover: false);
        Attempt(h, "/dest/F", AlbumCoverFetchStatus.Failed, h.Now.AddHours(10));
        Folder(h, "/dest/G", cover: false);
        Attempt(h, "/dest/G", AlbumCoverFetchStatus.NotFound, h.Now.AddHours(-1));
        Folder(h, "/dest/U", cover: false, s => s.IsUnreleased = true);
        Folder(h, "/dest/Demo", cover: false, s => s.OwnerUserId = WellKnownUsers.DemoId);
        return h;
    }

    [Fact]
    public async Task Album_folders_are_sorted_by_whether_they_have_a_cover_and_what_the_sweep_has_tried()
    {
        using var h = Library();

        var detail = await h.DescribeAsync(BackgroundJobIds.CoverArt);

        var panel = detail.Panel<BreakdownPanelDto>("Album folders by cover");
        Assert.Equal(7, panel.Total);
        Assert.Equal(1, panel.Segment("HasCover"));
        Assert.Equal(1, panel.Segment("UpgradeWaiting"));
        Assert.Equal(1, panel.Segment("UpgradeNotFound"));
        Assert.Equal(1, panel.Segment("MissingUntried"));
        Assert.Equal(2, panel.Segment("MissingNotFound"));
        Assert.Equal(1, panel.Segment("MissingFailed"));
        Assert.Equal(3, detail.Summary.Headline.Value);
        Assert.Equal(7, detail.Summary.Headline.Of);
    }

    [Fact]
    public async Task Folders_due_now_are_the_ones_the_sweep_would_take()
    {
        using var h = Library();

        var figures = (await h.DescribeAsync(BackgroundJobIds.CoverArt)).Panel<FiguresPanelDto>("The sweep");

        Assert.Equal(3, figures.Item("Folders due now").Value);
        Assert.Equal(h.Now.AddHours(10), figures.Item("Soonest retry").AtUtc);
        Assert.Equal(25, figures.Item("Folders per sweep").Value);
    }

    [Fact]
    public async Task Attempts_for_folders_the_sweep_no_longer_considers_are_left_out()
    {
        using var h = Library();
        Attempt(h, "/dest/Gone", AlbumCoverFetchStatus.Failed, h.Now.AddHours(1));
        Attempt(h, "/dest/A", AlbumCoverFetchStatus.NotFound, h.Now.AddHours(2));

        var detail = await h.DescribeAsync(BackgroundJobIds.CoverArt);

        Assert.Equal(h.Now.AddHours(10), detail.Panel<FiguresPanelDto>("The sweep").Item("Soonest retry").AtUtc);
        var today = detail.Panel<SeriesPanelDto>("What the sweep did each day").Points[^1].Values;
        Assert.Equal(2, today[1]);
        Assert.Equal(1, today[2]);
    }

    [Fact]
    public async Task With_spotify_covers_off_a_cover_archive_cover_is_just_a_cover()
    {
        using var h = Library();
        h.Enricher.EnableSpotifyCovers = false;

        var panel = (await h.DescribeAsync(BackgroundJobIds.CoverArt)).Panel<BreakdownPanelDto>("Album folders by cover");

        Assert.Equal(3, panel.Segment("HasCover"));
        Assert.Equal(0, panel.Segment("UpgradeWaiting"));
    }

    [Fact]
    public async Task Written_covers_and_failed_attempts_land_on_their_days()
    {
        using var h = Library();

        var detail = await h.DescribeAsync(BackgroundJobIds.CoverArt);

        var series = detail.Panel<SeriesPanelDto>("What the sweep did each day");
        var today = series.Points[^1].Values;
        Assert.Equal(2, today[0]);
        Assert.Equal(2, today[1]);
        Assert.Equal(1, today[2]);
        Assert.Equal(1, today[3]);
        Assert.Equal(2, detail.Summary.Trend[^1]);
    }

    [Fact]
    public async Task The_job_is_off_when_the_fetch_is_switched_off_and_reports_the_sweeps_state_otherwise()
    {
        using var h = new BackgroundWorkHarness();
        h.Status.Idle(BackgroundJobIds.CoverArt, h.Now.AddHours(6));
        var idle = await h.SummarizeAsync(BackgroundJobIds.CoverArt);
        Assert.Equal(BackgroundJobState.Idle, idle.State);
        Assert.Equal(h.Now.AddHours(6), idle.NextRunAtUtc);

        h.Enricher.EnableExternalCoverArtFetch = false;
        Assert.Equal(BackgroundJobState.Off, (await h.SummarizeAsync(BackgroundJobIds.CoverArt)).State);
    }
}
