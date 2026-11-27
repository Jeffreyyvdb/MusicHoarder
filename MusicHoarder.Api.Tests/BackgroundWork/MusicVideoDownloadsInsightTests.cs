using MusicHoarder.Api.Auth;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Storage;
using MusicHoarder.Api.Tests.Auth;
using static MusicHoarder.Api.Tests.BackgroundWork.MusicVideoTestData;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class MusicVideoDownloadsInsightTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static SongMetadata Add(MusicHoarderDbContext db, int n, Action<SongMetadata>? configure = null)
        => BackgroundWorkTestData.AddSong(db, n, configure);

    private static StorageUsageSnapshot SnapshotWithVideos(long bytes, DateTime at) => new(
        at, 0, 0, 0, 0, [], [], [new StorageBucket(CategoryKeys.Videos, bytes, 12)], [],
        new StorageDuplicates(0, 0), new StorageLyrics(0, 0), new StorageReclaimable(0, 0, null));

    private static BackgroundWorkHarness Automatic()
    {
        var h = new BackgroundWorkHarness();
        h.Settings.AutoDownloadVideos = true;
        h.Enricher.MusicVideoAutoDownloadMinFreeGb = 20;
        h.Disk.Available = 200 * Gb;
        return h;
    }

    [Fact]
    public async Task The_headline_is_the_ready_videos_with_what_they_take_on_disk_and_the_trend_is_when_they_arrived()
    {
        using var h = Automatic();
        h.Seed(db =>
        {
            SeedAdmin(db);
            db.SongMusicVideos.AddRange(
                Video(Add(db, 1), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now.AddHours(-2)),
                Video(Add(db, 2), MusicVideoStatus.Ready, MusicVideoPick.Requested, h.Now.AddDays(-1)),
                Video(Add(db, 3), MusicVideoStatus.Ready, MusicVideoPick.Requested, h.Now.AddDays(-60)),
                Video(Add(db, 4), MusicVideoStatus.Fetching, MusicVideoPick.Automatic),
                Video(Add(db, 5), MusicVideoStatus.Failed, MusicVideoPick.Automatic));
        });
        h.Storage.Complete(SnapshotWithVideos((long)(2.5 * Gb), h.Now));

        var summary = await h.SummarizeAsync(BackgroundJobIds.MusicVideoDownloads);

        Assert.Equal("Music video downloads", summary.Title);
        Assert.Equal(3, summary.Headline.Value);
        Assert.Equal("music videos are ready to play, 2.5 GB on disk", summary.Headline.Caption);
        Assert.Equal([1.0, 1], summary.Trend.TakeLast(2).Reverse());
        Assert.Equal(h.Now.AddHours(-2), summary.LastWorkedAtUtc);
    }

    [Fact]
    public async Task With_the_toggle_off_the_job_is_off_and_says_picks_are_only_queued_by_hand()
    {
        using var h = Automatic();
        h.Settings.AutoDownloadVideos = false;

        var summary = await h.SummarizeAsync(BackgroundJobIds.MusicVideoDownloads);

        Assert.Equal(BackgroundJobState.Off, summary.State);
        Assert.Contains("only fetched when someone picks one", summary.StateLabel);
    }

    [Fact]
    public async Task Free_space_under_the_floor_makes_it_wait_and_says_how_much_is_free()
    {
        using var h = Automatic();
        h.Disk.Available = 12 * Gb;

        var summary = await h.SummarizeAsync(BackgroundJobIds.MusicVideoDownloads);

        Assert.Equal(BackgroundJobState.Waiting, summary.State);
        Assert.Equal("Waiting for disk space: 12 GB free on the videos drive, and the floor is 20 GB.", summary.StateLabel);
    }

    [Fact]
    public async Task A_drive_that_cannot_be_read_waits_too_because_the_prefetch_queues_nothing_then()
    {
        using var h = Automatic();
        h.Disk.Available = null;

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads);

        Assert.Equal(BackgroundJobState.Waiting, detail.Summary.State);
        var free = detail.Panel<FiguresPanelDto>("Downloads in numbers").Item("Free on the videos drive");
        Assert.Null(free.Value);
        Assert.Equal(BackgroundTone.Warning, free.Tone);
    }

    [Fact]
    public async Task A_fetch_in_progress_is_not_hidden_by_a_short_drive()
    {
        using var h = Automatic();
        h.Disk.Available = 1 * Gb;
        h.Status.Working(BackgroundJobIds.MusicVideoDownloads);

        Assert.Equal(BackgroundJobState.Working, (await h.SummarizeAsync(BackgroundJobIds.MusicVideoDownloads)).State);
    }

    [Fact]
    public async Task With_room_and_the_toggle_on_it_is_what_the_worker_reported()
    {
        using var h = Automatic();

        Assert.Equal(BackgroundJobState.Unknown, (await h.SummarizeAsync(BackgroundJobIds.MusicVideoDownloads)).State);
        h.Status.Idle(BackgroundJobIds.MusicVideoDownloads, null, worked: true);
        Assert.Equal(BackgroundJobState.Idle, (await h.SummarizeAsync(BackgroundJobIds.MusicVideoDownloads)).State);
    }

    [Fact]
    public async Task Fetched_videos_are_counted_per_day_by_who_chose_them_and_every_row_has_a_state()
    {
        using var h = Automatic();
        h.Seed(db =>
        {
            SeedAdmin(db);
            db.SongMusicVideos.AddRange(
                Video(Add(db, 1), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now.AddHours(-2)),
                Video(Add(db, 2), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now.AddHours(-1)),
                Video(Add(db, 3), MusicVideoStatus.Ready, MusicVideoPick.Requested, h.Now.AddDays(-2)),
                Video(Add(db, 4), MusicVideoStatus.Ready, MusicVideoPick.Requested, h.Now.AddDays(-45)),
                Video(Add(db, 5), MusicVideoStatus.Fetching, MusicVideoPick.Automatic),
                Video(Add(db, 6), MusicVideoStatus.Failed, MusicVideoPick.Automatic));
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads);

        var fetched = detail.Panel<SeriesPanelDto>("Videos fetched per day");
        Assert.Equal(30, fetched.Points.Count);
        Assert.Equal(["automatic", "requested"], fetched.Series.Select(s => s.Key));
        Assert.Equal([2.0, 0], fetched.Points[^1].Values);
        Assert.Equal([0.0, 1], fetched.Points[^3].Values);
        Assert.Equal(3, fetched.Points.Sum(p => p.Values.Sum()));

        var states = detail.Panel<BreakdownPanelDto>("Where every video stands");
        Assert.Equal(6, states.Total);
        Assert.Equal(2, states.Segment("ready-automatic"));
        Assert.Equal(2, states.Segment("ready-requested"));
        Assert.Equal(1, states.Segment("fetching"));
        Assert.Equal(1, states.Segment("failed"));
    }

    [Fact]
    public async Task Confident_picks_waiting_are_clips_on_songs_with_no_video_row_and_their_size_is_the_picks()
    {
        using var h = Automatic();
        h.Seed(db =>
        {
            SeedAdmin(db);
            var at = h.Now.AddDays(-3);
            var failedVideo = Add(db, 4);
            var hasVideo = Add(db, 5);
            var renamed = Found(Add(db, 6), at, null, Confident());
            renamed.QueryKey = "an older search";
            var overdue = Found(Add(db, 7), at, h.Now.AddHours(-1), Confident());
            db.SongVideoCandidateSets.AddRange(
                Found(Add(db, 1), at, null, Confident(bytes: 100_000_000)),
                Found(Add(db, 2), at, null, Confident(bytes: 50_000_000)),
                Found(Add(db, 3), at, null, Weak()),
                Found(failedVideo, at, null, Confident()),
                Found(hasVideo, at, null, Confident()),
                renamed,
                overdue);
            db.SongMusicVideos.AddRange(
                Video(failedVideo, MusicVideoStatus.Failed, MusicVideoPick.Automatic),
                Video(hasVideo, MusicVideoStatus.Ready, MusicVideoPick.Requested, h.Now));
        });
        h.Storage.Complete(SnapshotWithVideos(3 * Gb, h.Now));
        h.Disk.Available = 150 * Gb;

        var figures = (await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads)).Panel<FiguresPanelDto>("Downloads in numbers");

        var waiting = figures.Item("Confident picks waiting");
        Assert.Equal(2, waiting.Value);
        Assert.Equal("143 MB to download", waiting.Sublabel);
        var free = figures.Item("Free on the videos drive");
        Assert.Equal(150 * Gb, free.Value);
        Assert.Equal("the floor is 20 GB", free.Sublabel);
        Assert.Equal(BackgroundTone.Neutral, free.Tone);
        Assert.Equal(3 * Gb, figures.Item("Clips on disk").Value);
        Assert.Equal(1, figures.Item("Failed videos").Value);
        Assert.Equal(400L * 1024 * 1024, figures.Item("Size limit per clip").Value);
    }

    [Fact]
    public async Task Waiting_picks_say_they_are_held_back_while_automatic_downloads_are_off()
    {
        using var h = Automatic();
        h.Settings.AutoDownloadVideos = false;
        h.Seed(db =>
        {
            SeedAdmin(db);
            db.SongVideoCandidateSets.Add(Found(Add(db, 1), h.Now.AddDays(-1), null, Confident(bytes: 80_000_000)));
        });

        var waiting = (await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads))
            .Panel<FiguresPanelDto>("Downloads in numbers").Item("Confident picks waiting");

        Assert.Equal(1, waiting.Value);
        Assert.Equal("76 MB, held back: automatic downloads are off", waiting.Sublabel);
    }

    [Fact]
    public async Task The_misses_are_banded_by_the_best_candidates_score_against_the_bar()
    {
        using var h = Automatic();
        h.Enricher.MusicVideoAutoDownloadMinScore = 75;
        h.Seed(db =>
        {
            SeedAdmin(db);
            var at = h.Now.AddDays(-3);
            var scores = new[] { 10, 34, 35, 54, 55, 60, 74, 80, 90 };
            for (var i = 0; i < scores.Length; i++)
                db.SongVideoCandidateSets.Add(Found(Add(db, i + 1), at, null, Weak(scores[i])));
            db.SongVideoCandidateSets.Add(Found(Add(db, 20), at, null, Confident()));
        });

        var bars = (await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads)).Panel<BarsPanelDto>("Why searches found no confident pick");

        Assert.Equal(["Under 35", "35 to 54", "55 to 74", "75 or more"], bars.Bars.Select(b => b.Label));
        Assert.Equal([2.0, 2, 3, 2], bars.Bars.Select(b => b.Value));
    }

    [Fact]
    public async Task The_banding_follows_the_options_bar()
    {
        using var h = Automatic();
        h.Enricher.MusicVideoAutoDownloadMinScore = 30;
        h.Seed(db => { SeedAdmin(db); db.SongVideoCandidateSets.Add(Found(Add(db, 1), h.Now.AddDays(-3), null, Weak(5))); });

        var bars = (await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads)).Panel<BarsPanelDto>("Why searches found no confident pick");

        Assert.Equal(["Under 10", "10 to 29", "30 or more"], bars.Bars.Select(b => b.Label));
        Assert.Equal([1.0, 0, 0], bars.Bars.Select(b => b.Value));
    }

    [Fact]
    public async Task Both_music_video_sheets_count_the_live_videos_of_every_admin_and_no_demo_ones()
    {
        using var h = new BackgroundWorkHarness(caller: TestCurrentUserAccessor.OwnerUser);
        h.Settings.AutoDownloadVideos = true;
        h.Disk.Available = 200 * Gb;
        var secondAdmin = Guid.NewGuid();
        h.Seed(db =>
        {
            SeedAdmin(db);
            db.Users.AddRange(
                new User { Id = secondAdmin, Email = "b@example.com", EmailNormalized = "B@EXAMPLE.COM", Role = UserRole.Admin },
                new User { Id = WellKnownUsers.DemoId, Email = "demo@example.com", EmailNormalized = "DEMO@EXAMPLE.COM", Role = UserRole.Demo });
            db.SaveChanges();
            db.SongMusicVideos.AddRange(
                Video(Add(db, 1), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now.AddHours(-1)),
                Video(Add(db, 2, s => s.DeletedAtUtc = h.Now.AddDays(-1)), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now.AddHours(-1)),
                Video(Add(db, 3, s => s.OwnerUserId = WellKnownUsers.DemoId), MusicVideoStatus.Ready, MusicVideoPick.Automatic, h.Now.AddHours(-1)),
                Video(Add(db, 4, s => s.OwnerUserId = secondAdmin), MusicVideoStatus.Ready, MusicVideoPick.Requested, h.Now.AddHours(-1)),
                Video(Add(db, 5, s => s.OwnerUserId = secondAdmin), MusicVideoStatus.Failed, MusicVideoPick.Automatic));
        });

        var search = (await h.DescribeAsync(BackgroundJobIds.MusicVideos)).Panel<BreakdownPanelDto>("What the last search found");
        var downloads = await h.DescribeAsync(BackgroundJobIds.MusicVideoDownloads);
        var states = downloads.Panel<BreakdownPanelDto>("Where every video stands");

        Assert.Equal(1, search.Segment("video-automatic"));
        Assert.Equal(1, search.Segment("video-requested"));
        Assert.Equal(search.Segment("video-automatic"), states.Segment("ready-automatic"));
        Assert.Equal(search.Segment("video-requested"), states.Segment("ready-requested"));
        Assert.Equal(1, states.Segment("failed"));
        Assert.Equal(2, downloads.Summary.Headline.Value);
        Assert.Equal(2, downloads.Panel<SeriesPanelDto>("Videos fetched per day").Points[^1].Values.Sum());
    }
}
