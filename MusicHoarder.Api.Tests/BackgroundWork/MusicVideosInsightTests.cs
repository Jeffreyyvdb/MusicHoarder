using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Download;
using static MusicHoarder.Api.Tests.BackgroundWork.MusicVideoTestData;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class MusicVideosInsightTests
{
    private static SongMetadata Add(MusicHoarderDbContext db, int n, Action<SongMetadata>? configure = null)
        => BackgroundWorkTestData.AddSong(db, n, configure);

    /// <summary>An admin, and one old song with a requested video, so the prefetch is not dormant (song 90).</summary>
    private static void SeedLibrary(BackgroundWorkHarness h, Action<MusicHoarderDbContext> more)
    {
        h.Seed(db =>
        {
            SeedAdmin(db);
            db.SongMusicVideos.Add(Video(Add(db, 90), MusicVideoStatus.Ready, MusicVideoPick.Requested, h.Now.AddDays(-400)));
            more(db);
        });
    }

    private static SeriesPanelDto Series(BackgroundJobDetailDto detail, string titleStartsWith)
        => detail.Panel<SeriesPanelDto>(titleStartsWith);

    private static double[] Column(SeriesPanelDto panel, int series)
        => [.. panel.Points.Select(p => p.Values[series])];

    [Fact]
    public async Task Every_song_the_search_considers_is_in_exactly_one_segment()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db =>
        {
            var searched = h.Now.AddDays(-2);
            var failedVideo = Add(db, 8);
            db.SongVideoCandidateSets.AddRange(
                Found(Add(db, 1), searched, null, Confident()),
                Found(Add(db, 2), searched, null, Weak(), Confident(score: 40)),
                Nothing(Add(db, 3), searched, emptySearches: 2),
                Set(Add(db, 4), "[]", searched, emptySearches: 1, error: "yt-dlp exited with 1"),
                Found(failedVideo, searched, null, Confident()));
            Add(db, 5);
            db.SongMusicVideos.AddRange(
                Video(Add(db, 6), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now.AddDays(-1)),
                Video(Add(db, 7), MusicVideoStatus.Fetching, MusicVideoPick.Requested),
                Video(failedVideo, MusicVideoStatus.Failed, MusicVideoPick.Automatic));
            // Not the search's business: deleted, not yet enriched, untitled, another account's.
            Add(db, 11, s => s.DeletedAtUtc = h.Now);
            Add(db, 12, s => s.EnrichmentStatus = EnrichmentStatus.Pending);
            Add(db, 13, s => s.Title = "");
            Add(db, 14, s => s.OwnerUserId = Guid.NewGuid());
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        var breakdown = detail.Panel<BreakdownPanelDto>("What the last search found");
        Assert.Equal(9, breakdown.Total);
        Assert.Equal(
            ["video-automatic", "video-requested", "clip", "no-clip", "empty", "never"],
            breakdown.Segments.Select(s => s.Key));
        Assert.Equal(1, breakdown.Segment("video-automatic"));
        Assert.Equal(2, breakdown.Segment("video-requested"));
        Assert.Equal(2, breakdown.Segment("clip"));
        Assert.Equal(1, breakdown.Segment("no-clip"));
        Assert.Equal(2, breakdown.Segment("empty"));
        Assert.Equal(1, breakdown.Segment("never"));

        // In scope: 1 to 5 and 8, whose failed video does not count; searched: all of those but 5.
        Assert.Equal(5, detail.Summary.Headline.Value);
        Assert.Equal(6, detail.Summary.Headline.Of);
    }

    [Fact]
    public async Task The_sheets_outcome_is_the_one_the_prefetch_stores_and_schedules_by()
    {
        using var h = new BackgroundWorkHarness();
        h.Enricher.MusicVideoCandidatePrefetchBatchSize = 50;
        var clock = BackgroundWorkTestData.Clock(h.Now);
        var downloader = new FakeMusicVideoDownloader
        {
            OnSuggest = request => request.Title == "Song 1"
                ?
                [
                    new MusicVideoCandidate("vid-confident", "Artist - Song 1 (Official Video)", "Artist", 200, 90, "https://t/1.jpg"),
                    new MusicVideoCandidate("vid-other", "Artist - Song 1 (Lyrics)", "Fan", 200, 50, null),
                ]
                : request.Title == "Song 2"
                    ? [new MusicVideoCandidate("vid-weak", "Artist - Song 2 (Lyrics)", "Fan", 200, 30, "https://t/2.jpg")]
                    : [],
        };
        var probe = new RealVideoProbe();

        SeedLibrary(h, db =>
        {
            foreach (var n in new[] { 1, 2, 3 })
                Add(db, n, s => s.Year = h.Now.Year);
            Add(db, 14, s => s.OwnerUserId = Guid.NewGuid());
        });
        using (var scope = h.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var cache = new MusicVideoCandidateCache(
                downloader, probe, Microsoft.Extensions.Options.Options.Create(h.Enricher), clock,
                NullLogger<MusicVideoCandidateCache>.Instance);
            var prefetch = new MusicVideoCandidatePrefetch(
                db, cache, new MusicVideoChannel(), downloader, h.Disk, h.Settings,
                Microsoft.Extensions.Options.Options.Create(h.Enricher), clock,
                NullLogger<MusicVideoCandidatePrefetch>.Instance);
            await prefetch.RunAsync(CancellationToken.None);

            // The pass searched exactly the songs the page calls in scope.
            Assert.Equal(
                ["1.mp3", "2.mp3", "3.mp3"],
                db.SongVideoCandidateSets.Select(c => c.Song.FileName).OrderBy(n => n).ToList());
        }

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        var breakdown = detail.Panel<BreakdownPanelDto>("What the last search found");
        Assert.Equal(1, breakdown.Segment("clip"));
        Assert.Equal(1, breakdown.Segment("no-clip"));
        Assert.Equal(1, breakdown.Segment("empty"));
        Assert.Equal(0, breakdown.Segment("never"));

        // A recent release: no clip is looked at again after 7 days, nothing found after 1, a clip after 30.
        var coming = Series(detail, "Searches coming due");
        Assert.Equal(1, Column(coming, 1)[7]);
        Assert.Equal(1, Column(coming, 2)[1]);
        Assert.Equal(0, Column(coming, 0).Sum());
        Assert.Equal(1, detail.Panel<FiguresPanelDto>("The search in numbers").Item("Due after 30 days").Value);
    }

    private sealed class RealVideoProbe : IMusicVideoProbe
    {
        public Task<MusicVideoProbeResult> ProbeAsync(string videoIdOrUrl, CancellationToken ct)
            => Task.FromResult(new MusicVideoProbeResult(
                videoIdOrUrl, "probed", "Channel", 200, MusicVideoMotion.RealVideo, 50_000_000, 30, 40, false, "https://i.ytimg.com/x.jpg"));
    }

    [Fact]
    public async Task The_map_follows_the_prefetchs_order_and_marks_the_next_song_in_line()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db =>
        {
            // Added 1 to 6 days ago, newest first is 1: songs 1 and 2 are waiting, 3 to 5 searched, 6 has a video.
            foreach (var n in new[] { 1, 2 })
                Add(db, n);
            foreach (var n in new[] { 3, 4, 5 })
                db.SongVideoCandidateSets.Add(Found(Add(db, n), h.Now.AddDays(-1), null, Weak()));
            db.SongMusicVideos.Add(Video(Add(db, 6), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now));
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        var map = detail.Panel<LibraryMapPanelDto>("Where the search has reached");
        Assert.Equal(7, map.Bins.Sum(b => b.Done + b.Waiting + b.Excluded));
        Assert.Equal(3, map.Bins.Sum(b => b.Done));
        Assert.Equal(2, map.Bins.Sum(b => b.Waiting));
        Assert.Equal(2, map.Bins.Sum(b => b.Excluded));
        Assert.True(map.Bins.SequenceEqual(map.Bins.OrderBy(b => b.FromUtc)));
        // Waiting songs 1 and 2 were indexed 1 and 2 days ago; the pass reaches the newer one first.
        Assert.Equal(h.Now.AddDays(-1), map.Markers.Single().AtUtc);
    }

    [Fact]
    public async Task Searches_per_day_count_each_song_on_the_day_of_its_latest_search_by_outcome()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db =>
        {
            db.SongVideoCandidateSets.AddRange(
                Found(Add(db, 1), h.Now.AddHours(-2), null, Confident()),
                Found(Add(db, 2), h.Now.AddHours(-3), null, Weak()),
                Found(Add(db, 3), h.Now.AddDays(-1), null, Weak()),
                Nothing(Add(db, 4), h.Now.AddDays(-3), 1),
                Found(Add(db, 5), h.Now.AddDays(-40), null, Weak()));
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        var searches = Series(detail, "Searches per day");
        Assert.Equal(30, searches.Points.Count);
        Assert.Equal(h.Now.Date, searches.Points[^1].StartUtc);
        Assert.Equal(["clip", "no-clip", "empty"], searches.Series.Select(s => s.Key));
        Assert.Equal([1.0, 1, 0], searches.Points[^1].Values);
        Assert.Equal([0.0, 1, 0], searches.Points[^2].Values);
        Assert.Equal([0.0, 0, 1], searches.Points[^4].Values);
        Assert.Equal(4, searches.Points.Sum(p => p.Values.Sum()));
        Assert.Contains("latest search", searches.Caption);
    }

    [Fact]
    public async Task The_schedule_chart_looks_forward_with_overdue_searches_on_today()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db =>
        {
            var searched = h.Now.AddDays(-5);
            db.SongVideoCandidateSets.AddRange(
                Found(Add(db, 1), searched, h.Now.AddDays(-2), Confident()),
                Found(Add(db, 2), searched, h.Now.AddHours(5), Weak()),
                Found(Add(db, 3), searched, h.Now.AddDays(3), Weak()),
                Found(Add(db, 4), searched, h.Now.AddDays(10), Confident()),
                Nothing(Add(db, 5), searched, 2, h.Now.AddDays(10)),
                Found(Add(db, 6), searched, h.Now.AddDays(45), Weak()));
            var untimed = Found(Add(db, 7), searched, null, Weak());
            untimed.NextSearchAtUtc = null;
            db.SongVideoCandidateSets.Add(untimed);
            // Has a video: no longer searched, so not coming due.
            db.SongVideoCandidateSets.Add(Found(Add(db, 8), searched, h.Now.AddDays(4), Weak()));
            db.SongMusicVideos.Add(Video(db.Songs.Single(s => s.FileName == "8.mp3"), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now));
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        var coming = Series(detail, "Searches coming due");
        Assert.Equal(30, coming.Points.Count);
        Assert.Equal(h.Now.Date, coming.Points[0].StartUtc);
        Assert.Equal(h.Now.Date.AddDays(29), coming.Points[^1].StartUtc);
        // Today: song 1 (overdue) as a clip, songs 2 (later today) and 7 (no date) as no clip.
        Assert.Equal([1.0, 2, 0], coming.Points[0].Values);
        Assert.Equal([0.0, 1, 0], coming.Points[3].Values);
        Assert.Equal([1.0, 0, 1], coming.Points[10].Values);
        Assert.Equal(6, coming.Points.Sum(p => p.Values.Sum()));
        Assert.Equal(1, detail.Panel<FiguresPanelDto>("The search in numbers").Item("Due after 30 days").Value);
    }

    [Fact]
    public async Task The_bars_group_empty_searches_by_how_many_in_a_row()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db =>
        {
            var at = h.Now.AddDays(-1);
            var depths = new[] { 0, 1, 1, 2, 3, 4, 5, 6, 9 };
            for (var i = 0; i < depths.Length; i++)
            {
                var song = Add(db, i + 1);
                db.SongVideoCandidateSets.Add(depths[i] == 0
                    ? Set(song, "[]", at, error: "yt-dlp exited with 1")
                    : Nothing(song, at, depths[i]));
            }
            db.SongVideoCandidateSets.Add(Found(Add(db, 20), at, null, Weak()));
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        var bars = detail.Panel<BarsPanelDto>("Empty searches in a row");
        Assert.Equal(["0", "1", "2", "3", "4 to 5", "6 or more"], bars.Bars.Select(b => b.Label));
        Assert.Equal([1.0, 2, 1, 1, 2, 2], bars.Bars.Select(b => b.Value));
    }

    [Fact]
    public async Task The_figures_count_what_is_due_and_what_failed_with_the_prefetchs_own_pass_size()
    {
        using var h = new BackgroundWorkHarness();
        h.Enricher.MusicVideoCandidatePrefetchBatchSize = 20;
        h.Enricher.MusicVideoCandidatePrefetchIntervalMinutes = 5;
        SeedLibrary(h, db =>
        {
            var at = h.Now.AddDays(-2);
            db.SongVideoCandidateSets.AddRange(
                Found(Add(db, 1), at, h.Now.AddDays(5), Confident()),
                Found(Add(db, 2), at, h.Now.AddDays(-1), Weak()),
                Set(Add(db, 3), "[]", at, h.Now.AddDays(1), emptySearches: 1, error: "yt-dlp exited with 1"),
                Nothing(Add(db, 4), at, 1, h.Now.AddDays(1)));
            Add(db, 5);
            Add(db, 6);
            // Searched under another title: the pass searches it again whatever its schedule says.
            var renamed = Add(db, 7);
            var stale = Found(renamed, at, h.Now.AddDays(5), Weak());
            stale.QueryKey = "an older search";
            db.SongVideoCandidateSets.Add(stale);
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideos);

        var figures = detail.Panel<FiguresPanelDto>("The search in numbers");
        Assert.Equal(7, figures.Item("Songs in scope").Value);
        Assert.Equal(5, figures.Item("Searched").Value);
        var due = figures.Item("Due now");
        Assert.Equal(4, due.Value);
        Assert.Equal("2 never searched, 2 coming round again", due.Sublabel);
        Assert.Equal(20, figures.Item("Per pass").Value);
        Assert.Contains("at most 5,760 a day", figures.Item("Per pass").Sublabel);
        Assert.Equal(1, figures.Item("Failed searches").Value);
    }

    [Fact]
    public async Task A_stored_list_is_read_once_per_search_not_once_per_refresh()
    {
        using var h = new BackgroundWorkHarness();
        var searched = h.Now.AddDays(-1);
        SeedLibrary(h, db => db.SongVideoCandidateSets.Add(Found(Add(db, 1), searched, null, Confident())));

        Assert.Equal(1, (await Breakdown(h)).Segment("clip"));

        // A list rewritten without a new search is not read again: the memo still says what the search found.
        h.Seed(db => db.SongVideoCandidateSets.Single().CandidatesJson = Json(Weak()));
        var again = await Breakdown(h);
        Assert.Equal(1, again.Segment("clip"));
        Assert.Equal(0, again.Segment("no-clip"));

        // A new search moves SearchedAtUtc, and the list is read again.
        h.Seed(db => db.SongVideoCandidateSets.Single().SearchedAtUtc = h.Now);
        var searchedAgain = await Breakdown(h);
        Assert.Equal(0, searchedAgain.Segment("clip"));
        Assert.Equal(1, searchedAgain.Segment("no-clip"));
    }

    [Fact]
    public async Task A_change_to_the_pick_gates_reads_every_list_again()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db => db.SongVideoCandidateSets.Add(Found(Add(db, 1), h.Now.AddDays(-1), null, Confident(score: 80))));

        Assert.Equal(1, (await Breakdown(h)).Segment("clip"));

        h.Enricher.MusicVideoAutoDownloadMinScore = 95;
        var stricter = await Breakdown(h);
        Assert.Equal(0, stricter.Segment("clip"));
        Assert.Equal(1, stricter.Segment("no-clip"));
    }

    private static async Task<BreakdownPanelDto> Breakdown(BackgroundWorkHarness h)
        => (await h.DescribeAsync(BackgroundJobIds.MusicVideos)).Panel<BreakdownPanelDto>("What the last search found");

    [Fact]
    public async Task The_row_counts_searches_and_does_not_need_the_lists_to_be_readable()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db =>
        {
            db.SongVideoCandidateSets.Add(Set(Add(db, 1), "not json at all", h.Now.AddHours(-1)));
            Add(db, 2);
            Add(db, 3);
        });

        var summary = await h.SummarizeAsync(BackgroundJobIds.MusicVideos);

        Assert.Equal("Music video search", summary.Title);
        Assert.Equal(1, summary.Headline.Value);
        Assert.Equal(3, summary.Headline.Of);
        Assert.Equal(1.0, summary.Trend[^1]);
        Assert.Equal(h.Now.AddHours(-1), summary.LastWorkedAtUtc);
    }

    [Fact]
    public async Task The_estimate_is_the_songs_never_searched_over_the_last_weeks_searches_per_day()
    {
        using var h = new BackgroundWorkHarness();
        SeedLibrary(h, db =>
        {
            // 4 searches over the last 2 days make 2 a day; 6 songs have never been searched.
            foreach (var (n, hours) in new[] { (1, 3), (2, 20), (3, 30), (4, 48) })
                db.SongVideoCandidateSets.Add(Found(Add(db, n), h.Now.AddHours(-hours), null, Weak()));
            foreach (var n in Enumerable.Range(5, 6))
                Add(db, n);
        });

        var headline = (await h.SummarizeAsync(BackgroundJobIds.MusicVideos)).Headline;

        Assert.Equal(4, headline.Value);
        Assert.Equal(10, headline.Of);
        Assert.Equal(h.Now.AddDays(3), headline.EtaUtc);
        Assert.Equal(
            "First pass through the library: 6 songs never searched, at the last 7 days' rate of 2 searches a day.",
            headline.EtaLabel);
    }

    [Fact]
    public async Task It_is_off_when_the_interval_is_zero_and_until_a_video_exists_or_downloads_are_on()
    {
        using var h = new BackgroundWorkHarness();
        h.Seed(db => { SeedAdmin(db); Add(db, 1); });

        var dormant = await h.SummarizeAsync(BackgroundJobIds.MusicVideos);
        Assert.Equal(BackgroundJobState.Off, dormant.State);
        Assert.Contains("Not started", dormant.StateLabel);

        h.Settings.AutoDownloadVideos = true;
        Assert.Equal(BackgroundJobState.Unknown, (await h.SummarizeAsync(BackgroundJobIds.MusicVideos)).State);

        h.Enricher.MusicVideoCandidatePrefetchIntervalMinutes = 0;
        var off = await h.SummarizeAsync(BackgroundJobIds.MusicVideos);
        Assert.Equal(BackgroundJobState.Off, off.State);
        Assert.Contains("MusicVideoCandidatePrefetchIntervalMinutes", off.StateLabel);
    }

    [Fact]
    public async Task The_state_is_what_the_prefetch_service_reported()
    {
        using var h = new BackgroundWorkHarness();
        h.Seed(db => { SeedAdmin(db); db.SongMusicVideos.Add(Video(Add(db, 1), MusicVideoStatus.Ready, MusicVideoPick.Requested)); });

        h.Status.Working(BackgroundJobIds.MusicVideos);
        Assert.Equal(BackgroundJobState.Working, (await h.SummarizeAsync(BackgroundJobIds.MusicVideos)).State);

        h.Status.Idle(BackgroundJobIds.MusicVideos, h.Now.AddMinutes(5), worked: true);
        var idle = await h.SummarizeAsync(BackgroundJobIds.MusicVideos);
        Assert.Equal(BackgroundJobState.Idle, idle.State);
        Assert.Equal(h.Now.AddMinutes(5), idle.NextRunAtUtc);
    }
}
