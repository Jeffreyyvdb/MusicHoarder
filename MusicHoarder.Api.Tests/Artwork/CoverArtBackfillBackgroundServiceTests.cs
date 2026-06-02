using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Artwork;

public class CoverArtBackfillBackgroundServiceTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static readonly byte[] FolderJpg = [0xFF, 0xD8, 0xFF, 9, 8];

    [Fact]
    public async Task Backfill_SetsHasCoverArt_AndWritesDestinationCovers()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            // built album, embedded art
            ["/source/a/track.mp3"] = new("audio"),
            ["/dest/Artist/2026 - Album A/01 - t.mp3"] = new("audio"),
            // built album, no art
            ["/source/b/track.mp3"] = new("audio"),
            ["/dest/Artist/2026 - Album B/01 - t.mp3"] = new("audio"),
            // built album, source folder image
            ["/source/c/track.mp3"] = new("audio"),
            ["/source/c/cover.jpg"] = new(FolderJpg),
            ["/dest/Artist/2026 - Album C/01 - t.mp3"] = new("audio"),
            // unreleased, embedded art
            ["/source/d/track.mp3"] = new("audio"),
            ["/dest/Artist/Unreleased/x.mp3"] = new("audio"),
            // not-yet-built, embedded art
            ["/source/e/track.mp3"] = new("audio")
        });

        var reader = new MapEmbeddedPictureReader
        {
            ["/source/a/track.mp3"] = new EmbeddedPicture(Png, "image/png"),
            ["/source/d/track.mp3"] = new EmbeddedPicture(Png, "image/png"),
            ["/source/e/track.mp3"] = new EmbeddedPicture(Png, "image/png")
        };

        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/source/a/track.mp3", dest: "/dest/Artist/2026 - Album A/01 - t.mp3", LibraryBuildStatus.Done),
            Song(2, "/source/b/track.mp3", dest: "/dest/Artist/2026 - Album B/01 - t.mp3", LibraryBuildStatus.Done),
            Song(3, "/source/c/track.mp3", dest: "/dest/Artist/2026 - Album C/01 - t.mp3", LibraryBuildStatus.Done),
            Song(4, "/source/d/track.mp3", dest: "/dest/Artist/Unreleased/x.mp3", LibraryBuildStatus.Done, isUnreleased: true),
            Song(5, "/source/e/track.mp3", dest: null, LibraryBuildStatus.Pending),
            Song(6, "/source/f/track.mp3", dest: "/dest/x.mp3", LibraryBuildStatus.Done, isSynthetic: true),
            Song(7, "/source/g/track.mp3", dest: "/dest/y.mp3", LibraryBuildStatus.Done, deleted: true));
        await db.SaveChangesAsync();

        await RunAsync(db, fs, reader);

        var songs = await db.Songs.IgnoreQueryFilters().OrderBy(s => s.Id).ToListAsync();

        // HasCoverArt reflects embedded OR source folder image; skips synthetic/deleted.
        Assert.True(songs[0].HasCoverArt);   // embedded
        Assert.False(songs[1].HasCoverArt);  // none
        Assert.True(songs[2].HasCoverArt);   // source folder image
        Assert.True(songs[3].HasCoverArt);   // unreleased still flagged
        Assert.True(songs[4].HasCoverArt);   // not built, still flagged
        Assert.False(songs[5].HasCoverArt);  // synthetic skipped
        Assert.False(songs[6].HasCoverArt);  // deleted skipped

        // Destination covers: only for built, non-unreleased albums.
        Assert.Equal(Png, await fs.File.ReadAllBytesAsync("/dest/Artist/2026 - Album A/cover.png"));
        Assert.Equal(FolderJpg, await fs.File.ReadAllBytesAsync("/dest/Artist/2026 - Album C/cover.jpg"));
        Assert.False(fs.File.Exists("/dest/Artist/2026 - Album B/cover.png")); // no art → none
        Assert.DoesNotContain(fs.Directory.EnumerateFiles("/dest/Artist/Unreleased"), f => f.Contains("cover"));

        // Marker recorded.
        var settings = await db.RuntimeSettings.SingleAsync();
        Assert.NotNull(settings.CoverArtBackfillCompletedAtUtc);
    }

    [Fact]
    public async Task Backfill_IsNoOp_WhenMarkerAlreadySet()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/a/track.mp3"] = new("audio"),
            ["/dest/Artist/2026 - Album A/01 - t.mp3"] = new("audio")
        });
        var reader = new MapEmbeddedPictureReader { ["/source/a/track.mp3"] = new EmbeddedPicture(Png, "image/png") };

        await using var db = CreateDbContext();
        db.RuntimeSettings.Add(new RuntimeSettings { CoverArtBackfillCompletedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.Songs.Add(Song(1, "/source/a/track.mp3", dest: "/dest/Artist/2026 - Album A/01 - t.mp3", LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        await RunAsync(db, fs, reader);

        // Already-marked → the track is not flagged and no cover is written.
        Assert.False((await db.Songs.SingleAsync()).HasCoverArt);
        Assert.False(fs.File.Exists("/dest/Artist/2026 - Album A/cover.png"));
    }

    [Fact]
    public async Task Backfill_DoesNotOverwriteExistingDestinationCover()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/source/a/track.mp3"] = new("audio"),
            ["/dest/Artist/2026 - Album A/01 - t.mp3"] = new("audio"),
            ["/dest/Artist/2026 - Album A/cover.jpg"] = new("user-provided")
        });
        var reader = new MapEmbeddedPictureReader { ["/source/a/track.mp3"] = new EmbeddedPicture(Png, "image/png") };

        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/source/a/track.mp3", dest: "/dest/Artist/2026 - Album A/01 - t.mp3", LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        await RunAsync(db, fs, reader);

        Assert.Equal("user-provided", fs.File.ReadAllText("/dest/Artist/2026 - Album A/cover.jpg"));
        Assert.False(fs.File.Exists("/dest/Artist/2026 - Album A/cover.png"));
        Assert.True((await db.Songs.SingleAsync()).HasCoverArt); // flag still set from embedded art
    }

    private static async Task RunAsync(MusicHoarderDbContext db, IFileSystem fs, IEmbeddedPictureReader reader)
    {
        var resolver = new CoverArtResolver(fs, reader);
        var writer = new AlbumCoverWriter(fs, resolver, NullLogger<AlbumCoverWriter>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            LibraryBuilderBatchSize = 100,
            LibraryBuilderWorkerConcurrency = 1,
            SmbConcurrency = 1
        });

        var service = new CoverArtBackfillBackgroundService(
            new FixedScopeFactory(db, fs, resolver, writer),
            options,
            NullLogger<CoverArtBackfillBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        // StartAsync returns at the first await; wait for the backfill to actually finish before asserting.
        if (service.ExecuteTask is { } executeTask)
        {
            await executeTask;
        }
        await service.StopAsync(CancellationToken.None);
    }

    private static SongMetadata Song(
        int id,
        string sourcePath,
        string? dest,
        LibraryBuildStatus status,
        bool isUnreleased = false,
        bool isSynthetic = false,
        bool deleted = false) => new()
    {
        Id = id,
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = "t",
        Artist = "Artist",
        AlbumArtist = "Artist",
        Album = "Album",
        DestinationPath = dest,
        LibraryBuildStatus = status,
        IsUnreleased = isUnreleased,
        IsSynthetic = isSynthetic,
        DeletedAtUtc = deleted ? DateTime.UtcNow : null
    };

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class MapEmbeddedPictureReader : Dictionary<string, EmbeddedPicture>, IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => TryGetValue(filePath, out var p) ? p : null;
        public bool HasPicture(string filePath) => ContainsKey(filePath);
    }

    // Every scope yields the same shared instances — mirrors how the production scopes resolve the
    // same singleton filesystem/resolver and a per-scope DbContext (here a single in-memory context).
    private sealed class FixedScopeFactory(
        MusicHoarderDbContext db,
        IFileSystem fs,
        ICoverArtResolver resolver,
        IAlbumCoverWriter writer) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            if (serviceType == typeof(IFileSystem)) return fs;
            if (serviceType == typeof(ICoverArtResolver)) return resolver;
            if (serviceType == typeof(IAlbumCoverWriter)) return writer;
            return null;
        }
    }
}
