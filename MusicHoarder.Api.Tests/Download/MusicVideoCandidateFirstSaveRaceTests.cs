using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Persistence;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// The picker and the background prefetch can search the same song at once, each finding no
/// <see cref="SongVideoCandidateSet"/> yet. Only Postgres enforces the one-set-per-song index, so
/// these run against a real server (<see cref="PostgresFactAttribute"/>).
/// </summary>
public class MusicVideoCandidateFirstSaveRaceTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Holds each context after its first read of the candidate sets until every racer has made
    /// one, so all of them have seen "no row" before any of them writes.
    /// </summary>
    private sealed class FirstReadGate(int racers) : DbCommandInterceptor
    {
        private int _arrived;
        private readonly ConcurrentDictionary<DbContext, bool> _seen = new();
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (eventData.Context is { } context
                && command.CommandText.Contains("FROM \"SongVideoCandidateSets\"")
                && _seen.TryAdd(context, true))
            {
                if (Interlocked.Increment(ref _arrived) == racers)
                    _open.TrySetResult();
                await _open.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            return result;
        }
    }

    private sealed class Captured : ILogger
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue($"{logLevel}: {formatter(state, exception)} {exception?.Message}");
    }

    private sealed class CapturedLogger<T>(Captured inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

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

    private readonly Captured _cacheLog = new();
    private readonly Captured _dbLog = new();

    private MusicVideoCandidateCache Cache(FakeMusicVideoDownloader downloader) => new(
        downloader, new InstantProbe(),
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src", DestinationDirectory = "/dst", MusicVideoProbeCandidates = 1,
        }),
        new FixedClock(Now), new CapturedLogger<MusicVideoCandidateCache>(_cacheLog));

    private static async Task<SongMetadata> SeedSongAsync(PostgresTestDatabase database)
    {
        await using var db = database.NewContext();
        var song = new SongMetadata
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = "/src/1.opus",
            FileName = "1.opus",
            Extension = ".opus",
            FileSizeBytes = 1,
            LastModifiedUtc = Now,
            IndexedAtUtc = Now,
            AcquiredAtUtc = Now.AddDays(-1),
            EnrichmentStatus = EnrichmentStatus.Matched,
            Artist = "Artist",
            Title = "Title",
            DurationSeconds = 200,
        };
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        return song;
    }

    /// <summary>Both racers found no row and one yielded, quietly: nothing failed, nothing was logged above Debug.</summary>
    private void AssertNoConflictReported()
    {
        var entry = Assert.Single(_cacheLog.Entries);
        Assert.StartsWith("Debug: Music video candidates for song", entry);
        Assert.Contains("stored by another writer first", entry);
        Assert.DoesNotContain(_dbLog.Entries, e => e.Contains("23505") || e.StartsWith("Error"));
    }

    [PostgresFact]
    public async Task Concurrent_first_searches_of_a_song_store_one_set_without_a_unique_violation()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var song = await SeedSongAsync(database);
        var cache = Cache(new FakeMusicVideoDownloader
        {
            OnSuggest = _ => [new MusicVideoCandidate("vid-aaaaa", "Song (Official Video)", "Artist", 200, 90, null)],
        });

        // The picker and the prefetch, each on its own context like two requests.
        var gate = new FirstReadGate(racers: 2);
        await using var picker = database.NewContext(_dbLog, gate);
        await using var prefetch = database.NewContext(_dbLog, gate);
        var lists = await Task.WhenAll(
            cache.SearchAsync(picker, song, currentVideoId: null, probeTop: true, default),
            cache.SearchAsync(prefetch, song, currentVideoId: null, probeTop: true, default));

        Assert.All(lists, l => Assert.Equal("vid-aaaaa", Assert.Single(l.Candidates).VideoId));
        AssertNoConflictReported();

        await using var read = database.NewContext();
        var set = Assert.Single(await read.SongVideoCandidateSets.AsNoTracking().ToListAsync());
        Assert.Null(set.Error);
        var served = await cache.GetAsync(read, song, default);
        Assert.NotNull(served);
        Assert.Equal(MusicVideoMotion.RealVideo, Assert.Single(served.Candidates).Motion);
    }

    [PostgresFact]
    public async Task Concurrent_first_failures_of_a_song_store_one_set_without_a_unique_violation()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var song = await SeedSongAsync(database);
        var cache = Cache(new FakeMusicVideoDownloader());

        var gate = new FirstReadGate(racers: 2);
        await using var a = database.NewContext(_dbLog, gate);
        await using var b = database.NewContext(_dbLog, gate);
        await Task.WhenAll(
            cache.RecordFailureAsync(a, song, "yt-dlp is not installed", default),
            cache.RecordFailureAsync(b, song, "yt-dlp is not installed", default));

        AssertNoConflictReported();
        await using var read = database.NewContext();
        var set = Assert.Single(await read.SongVideoCandidateSets.AsNoTracking().ToListAsync());
        Assert.Equal("yt-dlp is not installed", set.Error);
        Assert.Equal("[]", set.CandidatesJson);
        Assert.Null(await cache.GetAsync(read, song, default)); // a failure is never served
    }
}
