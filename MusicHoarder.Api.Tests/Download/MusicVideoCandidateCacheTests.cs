using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Download;

public class MusicVideoCandidateCacheTests
{
    private static readonly Guid Admin = WellKnownUsers.OwnerId;
    private static readonly Guid Member = new("11111111-2222-3333-4444-555555555555");

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class FakeProbe : IMusicVideoProbe
    {
        public List<string> Calls { get; } = [];
        public Func<string, MusicVideoProbeResult>? OnProbe { get; set; }

        public Task<MusicVideoProbeResult> ProbeAsync(string videoIdOrUrl, CancellationToken ct)
        {
            Calls.Add(videoIdOrUrl);
            return Task.FromResult(OnProbe?.Invoke(videoIdOrUrl) ?? Measured(videoIdOrUrl, MusicVideoMotion.RealVideo));
        }
    }

    private static MusicVideoProbeResult Measured(string id, MusicVideoMotion motion) =>
        new(id, $"probed {id}", "Channel", 200, motion, 50_000_000, 30, 40, false, "https://i.ytimg.com/x.jpg");

    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly Clock _clock = new(Now);
    private readonly FakeMusicVideoDownloader _downloader = new()
    {
        OnSuggest = _ =>
        [
            new MusicVideoCandidate("vid-aaaaa", "Song (Official Video)", "Artist", 200, 90, "https://t/1.jpg"),
            new MusicVideoCandidate("vid-bbbbb", "Song (Lyrics)", "Fan", 201, 60, "https://t/2.jpg"),
            new MusicVideoCandidate("vid-ccccc", "Song (Audio)", "Artist - Topic", 200, 40, null),
            new MusicVideoCandidate("vid-ddddd", "Song live", "Artist", 260, 20, "https://t/4.jpg"),
        ],
    };
    private readonly FakeProbe _probe = new();
    private readonly MusicEnricherOptions _options = new()
    {
        SourceDirectory = "/src",
        DestinationDirectory = "/dst",
        MusicVideoProbeCandidates = 2,
    };

    private MusicVideoCandidateCache Cache() => new(
        _downloader, _probe, Microsoft.Extensions.Options.Options.Create(_options), _clock,
        NullLogger<MusicVideoCandidateCache>.Instance);

    private MusicVideoCandidatePrefetch Prefetch(MusicHoarderDbContext db) => new(
        db, Cache(), Microsoft.Extensions.Options.Options.Create(_options), _clock,
        NullLogger<MusicVideoCandidatePrefetch>.Instance);

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task SeedUsersAsync(MusicHoarderDbContext db, bool withAVideo = true)
    {
        db.Users.AddRange(
            new User { Id = Admin, Email = "a@example.com", EmailNormalized = "A@EXAMPLE.COM", Role = UserRole.Admin },
            new User { Id = Member, Email = "m@example.com", EmailNormalized = "M@EXAMPLE.COM", Role = UserRole.Member });
        if (withAVideo)
        {
            // The prefetch runs only on an instance that uses music videos: an old song with one.
            db.Songs.Add(Song(99, acquired: Now.AddDays(-400)));
            db.SongMusicVideos.Add(new SongMusicVideo { SongId = 99, Status = MusicVideoStatus.Ready, FilePath = "/v/99.mp4" });
        }
        await db.SaveChangesAsync();
    }

    private static SongMetadata Song(int id, Guid? owner = null, DateTime? acquired = null,
        EnrichmentStatus status = EnrichmentStatus.Matched) => new()
    {
        Id = id,
        OwnerUserId = owner ?? Admin,
        SourcePath = $"/src/{id}.opus",
        FileName = $"{id}.opus",
        Extension = ".opus",
        FileSizeBytes = 1,
        LastModifiedUtc = Now,
        IndexedAtUtc = Now,
        AcquiredAtUtc = acquired ?? Now.AddDays(-1),
        EnrichmentStatus = status,
        Artist = "Artist",
        Title = $"Title {id}",
        DurationSeconds = 200,
    };

    [Fact]
    public async Task Prefetch_searches_and_measures_a_new_song_so_the_picker_opens_on_a_list()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        db.Songs.Add(Song(1));
        await db.SaveChangesAsync();

        Assert.Equal(1, await Prefetch(db).RunAsync(default));

        // Only the leading MusicVideoProbeCandidates are probed.
        Assert.Equal(["vid-aaaaa", "vid-bbbbb"], _probe.Calls);

        var result = await MusicVideoEndpoints.GetVideoCandidates(
            1, refresh: null, db, Cache(), Microsoft.Extensions.Options.Options.Create(_options), default);
        var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoCandidatesDto>>(result);
        Assert.True(ok.Value!.Remembered);
        Assert.Single(_downloader.SuggestCalls); // the picker did not search again
        Assert.Equal(["RealVideo", "RealVideo", "Unknown", "Unknown"], ok.Value.Candidates.Select(c => c.Motion));
        Assert.Equal(50_000_000, ok.Value.Candidates[0].EstimatedBytes);
        Assert.False(ok.Value.Candidates[2].HasThumbnail);
    }

    [Fact]
    public async Task Prefetch_skips_songs_that_are_old_unenriched_a_members_or_already_have_a_video()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        db.Songs.AddRange(
            Song(1, acquired: Now.AddDays(-40)),          // outside the window
            Song(2, status: EnrichmentStatus.Pending),    // its title may still change
            Song(3, owner: Member),                       // the picker is admin-only
            Song(4),                                      // has a video
            Song(5));                                     // its video failed: wanted
        db.SongMusicVideos.AddRange(
            new SongMusicVideo { SongId = 4, Status = MusicVideoStatus.Ready, FilePath = "/v/4.mp4" },
            new SongMusicVideo { SongId = 5, Status = MusicVideoStatus.Failed });
        await db.SaveChangesAsync();

        Assert.Equal(1, await Prefetch(db).RunAsync(default));
        Assert.Equal([5], await db.SongVideoCandidateSets.Select(c => c.SongId).ToListAsync());

        // Nothing left to do on the next pass.
        Assert.Equal(0, await Prefetch(db).RunAsync(default));
        Assert.Single(_downloader.SuggestCalls);
    }

    [Fact]
    public async Task A_renamed_song_is_searched_again_and_its_old_list_is_not_served()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        db.Songs.Add(Song(1));
        await db.SaveChangesAsync();
        await Prefetch(db).RunAsync(default);

        var song = await db.Songs.SingleAsync(s => s.Id == 1);
        song.Title = "Corrected title";
        await db.SaveChangesAsync();

        Assert.Null(await Cache().GetAsync(db, song, default));
        Assert.Equal(1, await Prefetch(db).RunAsync(default));
        Assert.Equal("Corrected title", _downloader.SuggestCalls[^1].Title);
        Assert.NotNull(await Cache().GetAsync(db, song, default));
    }

    [Fact]
    public async Task A_list_older_than_the_cache_days_is_not_served()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        var song = Song(1);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await Cache().SearchAsync(db, song, null, probeTop: false, default);

        _clock.Now = Now.AddDays(_options.MusicVideoCandidateCacheDays + 1);
        Assert.Null(await Cache().GetAsync(db, song, default));
    }

    [Fact]
    public async Task A_failed_search_is_recorded_and_retried_only_after_a_day()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        db.Songs.Add(Song(1));
        await db.SaveChangesAsync();
        _downloader.OnSuggest = _ => throw new InvalidOperationException("yt-dlp not found");

        Assert.Equal(0, await Prefetch(db).RunAsync(default));
        var set = await db.SongVideoCandidateSets.SingleAsync();
        Assert.Equal("yt-dlp not found", set.Error);
        Assert.Null(await Cache().GetAsync(db, await db.Songs.SingleAsync(s => s.Id == 1), default));

        await Prefetch(db).RunAsync(default);
        Assert.Single(_downloader.SuggestCalls);

        _clock.Now = Now.AddDays(1).AddMinutes(1);
        await Prefetch(db).RunAsync(default);
        Assert.Equal(2, _downloader.SuggestCalls.Count);
    }

    [Fact]
    public async Task A_retry_in_the_background_service_replaces_the_failed_set_and_is_not_searched_again()
    {
        var dbOptions = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        MusicHoarderDbContext BackgroundScope() =>
            new(dbOptions, new HttpContextCurrentUserAccessor(new HttpContextAccessor()));
        await using var unfiltered = new MusicHoarderDbContext(dbOptions);
        await SeedUsersAsync(unfiltered);
        unfiltered.Songs.Add(Song(1));
        await unfiltered.SaveChangesAsync();

        var answers = _downloader.OnSuggest;
        _downloader.OnSuggest = _ => throw new InvalidOperationException("YouTube said no");
        await using (var db = BackgroundScope())
            Assert.Equal(0, await Prefetch(db).RunAsync(default));

        _downloader.OnSuggest = answers;
        _clock.Now = Now.AddDays(1).AddMinutes(1);
        await using (var db = BackgroundScope())
            Assert.Equal(1, await Prefetch(db).RunAsync(default));

        var set = Assert.Single(await unfiltered.SongVideoCandidateSets.AsNoTracking().ToListAsync());
        Assert.Null(set.Error);

        await using (var db = BackgroundScope())
            Assert.Equal(0, await Prefetch(db).RunAsync(default));
        Assert.Equal(2, _downloader.SuggestCalls.Count);
    }

    [Fact]
    public async Task Prefetch_stops_a_pass_after_three_failures_in_a_row()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        for (var i = 1; i <= 6; i++) db.Songs.Add(Song(i));
        await db.SaveChangesAsync();
        _downloader.OnSuggest = _ => throw new InvalidOperationException("YouTube said no");

        await Prefetch(db).RunAsync(default);
        Assert.Equal(3, _downloader.SuggestCalls.Count);
    }

    [Fact]
    public async Task A_probe_verdict_is_remembered_and_survives_a_new_search()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        var song = Song(1);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await Cache().SearchAsync(db, song, null, probeTop: false, default);

        _probe.OnProbe = id => Measured(id, MusicVideoMotion.Static);
        await MusicVideoEndpoints.ProbeVideoCandidate(1, "vid-ddddd", db, _probe, Cache(), default);

        var remembered = await Cache().GetAsync(db, song, default);
        Assert.Equal(MusicVideoMotion.Static, remembered!.Candidates.Single(c => c.VideoId == "vid-ddddd").Motion);

        // "Search again" keeps what was already measured instead of probing it a second time.
        var result = await MusicVideoEndpoints.GetVideoCandidates(
            1, refresh: true, db, Cache(), Microsoft.Extensions.Options.Options.Create(_options), default);
        var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoCandidatesDto>>(result);
        Assert.False(ok.Value!.Remembered);
        Assert.Equal(2, _downloader.SuggestCalls.Count);
        Assert.Equal("Static", ok.Value.Candidates.Single(c => c.VideoId == "vid-ddddd").Motion);
    }

    [Fact]
    public async Task A_failed_probe_leaves_the_candidate_unmeasured()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        var song = Song(1);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        _probe.OnProbe = id => new MusicVideoProbeResult(
            id, "", "", null, MusicVideoMotion.Unknown, null, null, null, false, null, Error: "timed out");

        await Cache().SearchAsync(db, song, null, probeTop: true, default);

        var remembered = await Cache().GetAsync(db, song, default);
        Assert.All(remembered!.Candidates, c => Assert.Equal(MusicVideoMotion.Unknown, c.Motion));
        Assert.Equal("Song (Official Video)", remembered.Candidates[0].Title);
    }

    [Fact]
    public async Task The_attached_video_is_offered_first_even_when_the_remembered_list_predates_it()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        var song = Song(1);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await Cache().SearchAsync(db, song, null, probeTop: false, default);
        db.SongMusicVideos.Add(new SongMusicVideo
        {
            SongId = 1, Status = MusicVideoStatus.Ready, YouTubeVideoId = "vid-zzzzz", FilePath = "/v/1.mp4",
        });
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.GetVideoCandidates(
            1, refresh: null, db, Cache(), Microsoft.Extensions.Options.Options.Create(_options), default);
        var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoCandidatesDto>>(result);
        Assert.Equal("vid-zzzzz", ok.Value!.Candidates[0].VideoId);
        Assert.True(ok.Value.Candidates[0].IsCurrent);
        Assert.Equal(5, ok.Value.Candidates.Count);
    }

    [Fact]
    public async Task Prefetch_does_nothing_on_an_instance_that_has_never_fetched_a_video()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db, withAVideo: false);
        db.Songs.Add(Song(1));
        await db.SaveChangesAsync();

        Assert.Equal(0, await Prefetch(db).RunAsync(default));
        Assert.Empty(_downloader.SuggestCalls);
    }

    [Fact]
    public async Task An_empty_search_is_not_remembered_and_counts_as_a_failure()
    {
        // A refused search (bot challenge, rate limit) comes back empty rather than throwing.
        await using var db = NewContext();
        await SeedUsersAsync(db);
        for (var i = 1; i <= 6; i++) db.Songs.Add(Song(i));
        await db.SaveChangesAsync();
        _downloader.OnSuggest = _ => [];

        Assert.Equal(0, await Prefetch(db).RunAsync(default));
        Assert.Equal(3, _downloader.SuggestCalls.Count);
        Assert.All(await db.SongVideoCandidateSets.ToListAsync(),
            set => Assert.Equal(MusicVideoCandidateCache.NoCandidatesError, set.Error));

        // The picker searches again instead of showing "no candidates" from memory.
        _downloader.OnSuggest = null;
        var song = await db.Songs.SingleAsync(s => s.Id == 1);
        Assert.Null(await Cache().GetAsync(db, song, default));
    }

    [Fact]
    public async Task A_search_that_finds_only_the_attached_video_is_not_remembered()
    {
        // The search puts the pinned current video first even when YouTube answered nothing.
        await using var db = NewContext();
        await SeedUsersAsync(db);
        var song = Song(1);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        _downloader.OnSuggest = r => [new MusicVideoCandidate(r.PinnedIdOrUrl!, "", "", null, 0, null)];

        var list = await Cache().SearchAsync(db, song, "vid-zzzzz", probeTop: true, default);

        Assert.Empty(list.Candidates);
        Assert.Equal(MusicVideoCandidateCache.NoCandidatesError, (await db.SongVideoCandidateSets.SingleAsync()).Error);
    }

    [Fact]
    public async Task An_empty_search_again_keeps_the_good_list_remembered_before()
    {
        await using var db = NewContext();
        await SeedUsersAsync(db);
        var song = Song(1);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        await Cache().SearchAsync(db, song, null, probeTop: false, default);

        _downloader.OnSuggest = _ => [];
        await Cache().SearchAsync(db, song, null, probeTop: false, default);

        Assert.Equal(4, (await Cache().GetAsync(db, song, default))!.Candidates.Count);
    }
}
