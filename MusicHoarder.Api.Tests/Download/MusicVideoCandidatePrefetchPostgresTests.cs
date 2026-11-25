using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
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
    public async Task A_pass_over_the_library_searches_newest_first_stores_the_schedule_and_queues_the_pick()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var seed = database.NewContext())
        {
            var withVideo = Song(0, Now.AddYears(-3));
            seed.Songs.AddRange(withVideo, Song(1, Now.AddYears(-1)), Song(2, Now.AddDays(-2)), Song(3, null));
            await seed.SaveChangesAsync();
            seed.SongMusicVideos.Add(new SongMusicVideo
            {
                SongId = withVideo.Id, Status = MusicVideoStatus.Ready, FilePath = "/v/0.mp4",
            });
            await seed.SaveChangesAsync();
        }

        var options = new MusicEnricherOptions
        {
            SourceDirectory = "/src", DestinationDirectory = "/dst", MusicVideoProbeCandidates = 1,
        };
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
                downloader, new InstantProbe(), Microsoft.Extensions.Options.Options.Create(options), clock,
                NullLogger<MusicVideoCandidateCache>.Instance);
            return await new MusicVideoCandidatePrefetch(
                db, cache, channel, downloader, new FakeFreeDiskSpace(),
                new FakeRuntimeSettings { AutoDownloadMusicVideos = true },
                Microsoft.Extensions.Options.Options.Create(options), clock,
                NullLogger<MusicVideoCandidatePrefetch>.Instance).RunAsync(default);
        }

        Assert.Equal(3, await PassAsync());

        // Newest first; the song with no acquisition stamp falls back to its index time (Now - 3 days).
        Assert.Equal(["Title 2", "Title 3", "Title 1"], downloader.SuggestCalls.Select(c => c.Title));

        await using var read = database.NewContext();
        var sets = await read.SongVideoCandidateSets.AsNoTracking().Include(c => c.Song).ToListAsync();
        Assert.Equal(3, sets.Count);
        Assert.All(sets, set => Assert.Equal(0, set.EmptySearches));
        // A recent release with a confident pick waits a month; the 2008 one half a year.
        Assert.Equal(Now.AddDays(30), sets.Single(c => c.Song.Title == "Title 2").NextSearchAtUtc);
        Assert.Equal(Now.AddDays(180), sets.Single(c => c.Song.Title == "Title 1").NextSearchAtUtc);

        var queued = await read.SongMusicVideos.AsNoTracking().Include(v => v.Song)
            .Where(v => v.Pick == MusicVideoPick.Automatic).ToListAsync();
        Assert.Equal(3, queued.Count);
        Assert.All(queued, v =>
        {
            Assert.Equal(MusicVideoStatus.Fetching, v.Status);
            Assert.Equal("vid-aaaaa", v.YouTubeVideoId);
        });
        Assert.Equal(3, channel.Reader.Count);

        // The songs now have a fetch in progress, so none is in scope.
        Assert.Equal(0, await PassAsync());
        Assert.Equal(3, downloader.SuggestCalls.Count);
    }
}
