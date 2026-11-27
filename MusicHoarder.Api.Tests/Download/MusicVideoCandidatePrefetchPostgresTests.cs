using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sync;
using MusicHoarder.Api.Tests.Persistence;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// The prefetch's own queries and the cache's first-write insert, on a real server: the library-wide
/// projection and its newest-first ordering, the columns the new migrations add, and an automatic
/// download's row. The in-memory provider runs none of that SQL.
/// </summary>
public class MusicVideoCandidatePrefetchPostgresTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed class InstantProbe : IMusicVideoProbe
    {
        public Task<MusicVideoProbeResult> ProbeAsync(string videoIdOrUrl, CancellationToken ct) =>
            Task.FromResult(new MusicVideoProbeResult(videoIdOrUrl, $"probed {videoIdOrUrl}", "Channel", 200,
                MusicVideoMotion.RealVideo, 50_000_000, 30, 40, false, null));
    }

    private static SongMetadata Song(int n, DateTime? acquired) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = $"/src/{n}.opus",
        FileName = $"{n}.opus",
        Extension = ".opus",
        FileSizeBytes = 1,
        LastModifiedUtc = Now,
        IndexedAtUtc = Now.AddDays(-n),
        AcquiredAtUtc = acquired,
        EnrichmentStatus = EnrichmentStatus.Matched,
        Artist = "Artist",
        Title = $"Title {n}",
        DurationSeconds = 200,
        ReleaseDate = n == 1 ? "2008" : null,
    };

    [PostgresFact]
    public async Task A_pass_over_the_library_prioritizes_my_tracks_stores_the_schedule_and_queues_the_pick()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var seed = database.NewContext())
        {
            var withVideo = Song(0, Now.AddYears(-3));
            var albumFill = Song(4, Now.AddHours(-1));
            albumFill.SourcePath = "/dl/4.opus";
            albumFill.DestinationPath = "/dst/Artist/Album/4.opus";
            albumFill.LibraryBuildStatus = LibraryBuildStatus.Done;
            albumFill.AcquisitionIntent = SongAcquisitionIntent.AlbumFill;

            var myTrack = Song(5, Now.AddYears(-2));
            myTrack.DestinationPath = "/dst/Artist/Album/5.opus";
            myTrack.LibraryBuildStatus = LibraryBuildStatus.Done;

            var dup = Song(6, Now.AddMinutes(-5));
            dup.IsDuplicate = true;

            seed.Songs.AddRange(withVideo, Song(1, Now.AddYears(-1)), Song(2, Now.AddDays(-2)), Song(3, null), albumFill, myTrack, dup);
            await seed.SaveChangesAsync();
            seed.SongMusicVideos.Add(new SongMusicVideo
            {
                SongId = withVideo.Id, Status = MusicVideoStatus.Ready, FilePath = "/v/0.mp4",
            });
            await seed.SaveChangesAsync();
        }

        var options = new MusicEnricherOptions
        {
            SourceDirectory = "/src", DestinationDirectory = "/dst", DownloadDirectory = "/dl", MusicVideoProbeCandidates = 1,
        };
        var syncOptions = new SyncOptions();
        var memo = new MusicVideoOutcomeMemo();
        var downloader = new FakeMusicVideoDownloader
        {
            OnSuggest = _ => [new MusicVideoCandidate("vid-aaaaa", "Song (Official Video)", "Artist", 200, 90, null)],
        };
        var clock = new FixedClock(Now);
        var channel = new MusicVideoChannel();

        async Task<int> PassAsync()
        {
            await using var db = database.NewContext();
            var cache = new MusicVideoCandidateCache(
                downloader, new InstantProbe(), Microsoft.Extensions.Options.Options.Create(options), clock, memo,
                NullLogger<MusicVideoCandidateCache>.Instance);
            return await new MusicVideoCandidatePrefetch(
                db, cache, channel, downloader, new FakeFreeDiskSpace(),
                new FakeRuntimeSettings { AutoDownloadMusicVideos = true },
                Microsoft.Extensions.Options.Options.Create(options),
                Microsoft.Extensions.Options.Options.Create(syncOptions),
                memo, clock,
                NullLogger<MusicVideoCandidatePrefetch>.Instance).RunAsync(default);
        }

        Assert.Equal(5, await PassAsync());

        // My tracks first (Title 5), then other built (Title 4), then unbuilt newest first (Title 2, Title 3, Title 1). Duplicate skipped.
        Assert.Equal(["Title 5", "Title 4", "Title 2", "Title 3", "Title 1"], downloader.SuggestCalls.Select(c => c.Title));

        await using var read = database.NewContext();
        var sets = await read.SongVideoCandidateSets.AsNoTracking().Include(c => c.Song).ToListAsync();
        Assert.Equal(5, sets.Count);
        Assert.All(sets, set => Assert.Equal(0, set.EmptySearches));
        // A recent release with a confident pick waits a month; the 2008 one half a year.
        Assert.Equal(Now.AddDays(30), sets.Single(c => c.Song.Title == "Title 2").NextSearchAtUtc);
        Assert.Equal(Now.AddDays(180), sets.Single(c => c.Song.Title == "Title 1").NextSearchAtUtc);

        var queued = await read.SongMusicVideos.AsNoTracking().Include(v => v.Song)
            .Where(v => v.Pick == MusicVideoPick.Automatic).ToListAsync();
        Assert.Equal(5, queued.Count);
        Assert.All(queued, v =>
        {
            Assert.Equal(MusicVideoStatus.Fetching, v.Status);
            Assert.Equal("vid-aaaaa", v.YouTubeVideoId);
        });
        Assert.Equal(5, channel.Reader.Count);

        // The songs now have a fetch in progress, so none is in scope.
        Assert.Equal(0, await PassAsync());
        Assert.Equal(5, downloader.SuggestCalls.Count);
    }

    [PostgresFact]
    public async Task Concurrent_manual_and_automatic_picks_settle_cleanly_with_one_queued_item()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var songIds = new List<int>();
        await using (var seed = database.NewContext())
        {
            var songs = Enumerable.Range(1, 10).Select(i => Song(i, Now.AddDays(-i))).ToList();
            seed.Songs.AddRange(songs);
            await seed.SaveChangesAsync();
            songIds.AddRange(songs.Select(s => s.Id));
        }

        // Race RequestAsync and TryPickAsync concurrently on the same song across 10 songs:
        // neither call may throw on IX_SongMusicVideos_SongId, and each song enqueues exactly once.
        foreach (var songId in songIds)
        {
            var channel = new MusicVideoChannel();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var manualTask = Task.Run(async () =>
            {
                await using var db = database.NewContext();
                await start.Task;
                return await MusicVideoFetchQueue.RequestAsync(db, channel, songId, "vid-manual", default);
            });
            var autoTask = Task.Run(async () =>
            {
                await using var db = database.NewContext();
                await start.Task;
                return await MusicVideoFetchQueue.TryPickAsync(db, channel, songId, "vid-auto", default, 50_000_000);
            });

            start.SetResult();
            var manual = await manualTask;
            var auto = await autoTask;

            Assert.Equal(MusicVideoStatus.Fetching, manual.Status);
            var queued = new List<MusicVideoWorkItem>();
            while (channel.Reader.TryRead(out var item))
                queued.Add(item);
            Assert.Single(queued);

            await using var read = database.NewContext();
            var row = await read.SongMusicVideos.IgnoreQueryFilters().SingleAsync(v => v.SongId == songId);
            Assert.Equal(MusicVideoStatus.Fetching, row.Status);
            if (auto is null)
                Assert.Equal(MusicVideoPick.Requested, row.Pick);
            else
                Assert.Equal(MusicVideoPick.Automatic, row.Pick);
        }
    }
}
