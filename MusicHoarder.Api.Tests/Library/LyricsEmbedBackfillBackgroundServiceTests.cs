using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;

namespace MusicHoarder.Api.Tests.Library;

public class LyricsEmbedBackfillBackgroundServiceTests
{
    [Fact]
    public async Task Backfill_RequeuesOnlyBuiltTracksWhoseFileIsMissingLyrics()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            // Done, DB has lyrics, file missing them → must be re-queued.
            Song(1, dest: "/dest/missing.flac", LibraryBuildStatus.Done, synced: "[00:01] hi"),
            // Done, DB has lyrics, file already carries them → left alone.
            Song(2, dest: "/dest/present.flac", LibraryBuildStatus.Done, plain: "hello"),
            // Done, no DB lyrics (instrumental/not-found) → not a candidate.
            Song(3, dest: "/dest/instrumental.flac", LibraryBuildStatus.Done),
            // Done, DB has lyrics, but synthetic (demo) → skipped.
            Song(4, dest: "/dest/demo.flac", LibraryBuildStatus.Done, plain: "x", isSynthetic: true),
            // Not yet built → not a candidate even with lyrics.
            Song(5, dest: null, LibraryBuildStatus.Pending, synced: "[00:01] hi"),
            // Done, DB has lyrics, file missing them, but deleted → skipped.
            Song(6, dest: "/dest/deleted.flac", LibraryBuildStatus.Done, plain: "y", deleted: true));
        await db.SaveChangesAsync();

        var reader = new MapLyricsReader
        {
            // Only #2 has embedded lyrics on disk; every other path reads as missing.
            ["/dest/present.flac"] = "hello",
        };

        await RunAsync(db, reader, available: true);

        var songs = await db.Songs.IgnoreQueryFilters().OrderBy(s => s.Id).ToListAsync();

        // #1: missing on disk → re-queued in place (force-rebuild signal set, destination kept).
        Assert.Equal(LibraryBuildStatus.Pending, songs[0].LibraryBuildStatus);
        Assert.Equal("/dest/missing.flac", songs[0].PreviousDestinationPath);
        Assert.Equal("/dest/missing.flac", songs[0].DestinationPath);

        // #2: already embedded → untouched.
        Assert.Equal(LibraryBuildStatus.Done, songs[1].LibraryBuildStatus);
        Assert.Null(songs[1].PreviousDestinationPath);

        // #3 no lyrics, #4 synthetic, #5 not built, #6 deleted → all left Done/Pending, never re-queued.
        Assert.Equal(LibraryBuildStatus.Done, songs[2].LibraryBuildStatus);
        Assert.Null(songs[2].PreviousDestinationPath);
        Assert.Equal(LibraryBuildStatus.Done, songs[3].LibraryBuildStatus);
        Assert.Equal(LibraryBuildStatus.Pending, songs[4].LibraryBuildStatus);
        Assert.Null(songs[4].PreviousDestinationPath);
        Assert.Equal(LibraryBuildStatus.Done, songs[5].LibraryBuildStatus);

        // #3 (no DB lyrics) is not even a candidate, so its file is never read.
        Assert.DoesNotContain("/dest/instrumental.flac", reader.RequestedPaths);

        Assert.NotNull((await db.RuntimeSettings.SingleAsync()).LyricsEmbedBackfillCompletedAtUtc);
    }

    [Fact]
    public async Task Backfill_IsNoOp_WhenMarkerAlreadySet()
    {
        await using var db = CreateDbContext();
        db.RuntimeSettings.Add(new RuntimeSettings
        {
            LyricsEmbedBackfillCompletedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.Songs.Add(Song(1, dest: "/dest/missing.flac", LibraryBuildStatus.Done, synced: "[00:01] hi"));
        await db.SaveChangesAsync();

        await RunAsync(db, new MapLyricsReader(), available: true);

        // Already marked → the missing-lyrics track is not re-queued.
        Assert.Equal(LibraryBuildStatus.Done, (await db.Songs.SingleAsync()).LibraryBuildStatus);
    }

    [Fact]
    public async Task Backfill_Defers_WhenDestinationOffline()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, dest: "/dest/missing.flac", LibraryBuildStatus.Done, synced: "[00:01] hi"));
        await db.SaveChangesAsync();

        await RunAsync(db, new MapLyricsReader(), available: false);

        // Offline → nothing re-queued and the marker stays unset so it retries next boot.
        Assert.Equal(LibraryBuildStatus.Done, (await db.Songs.SingleAsync()).LibraryBuildStatus);
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
    }

    private static async Task RunAsync(MusicHoarderDbContext db, IEmbeddedLyricsReader reader, bool available)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            LibraryBuilderBatchSize = 100,
            SmbConcurrency = 2,
        });

        var service = new LyricsEmbedBackfillBackgroundService(
            new FixedScopeFactory(db),
            reader,
            new FakeAvailability(available),
            options,
            NullLogger<LyricsEmbedBackfillBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is { } executeTask)
        {
            await executeTask;
        }
        await service.StopAsync(CancellationToken.None);
    }

    private static SongMetadata Song(
        int id,
        string? dest,
        LibraryBuildStatus status,
        string? synced = null,
        string? plain = null,
        bool isSynthetic = false,
        bool deleted = false) => new()
    {
        Id = id,
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = $"/source/{id}.flac",
        FileName = $"{id}.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = "t",
        Artist = "Artist",
        Album = "Album",
        EnrichmentStatus = EnrichmentStatus.Matched,
        DestinationPath = dest,
        LibraryBuildStatus = status,
        SyncedLyrics = synced,
        PlainLyrics = plain,
        LyricsStatus = synced is not null || plain is not null ? LyricsStatus.Fetched : LyricsStatus.NotFound,
        IsSynthetic = isSynthetic,
        DeletedAtUtc = deleted ? DateTime.UtcNow : null,
    };

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class MapLyricsReader : Dictionary<string, string>, IEmbeddedLyricsReader
    {
        public List<string> RequestedPaths { get; } = [];

        public string? ReadEmbeddedLyrics(string path)
        {
            lock (RequestedPaths) RequestedPaths.Add(path);
            return TryGetValue(path, out var lyrics) ? lyrics : null;
        }
    }

    private sealed class FakeAvailability(bool available) : IDirectoryAvailability
    {
        public DirectoryAvailabilitySnapshot Current { get; } =
            new(available, available, "/source", "/dest", DateTime.UtcNow);

        public Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);
    }

    private sealed class FixedScopeFactory(MusicHoarderDbContext db)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }

        public object? GetService(Type serviceType)
            => serviceType == typeof(MusicHoarderDbContext) ? db : null;
    }
}
