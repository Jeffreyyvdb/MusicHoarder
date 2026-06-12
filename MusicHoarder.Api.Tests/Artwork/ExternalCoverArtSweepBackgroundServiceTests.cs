using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Artwork;

public class ExternalCoverArtSweepBackgroundServiceTests
{
    [Fact]
    public async Task SweepsOneAttemptPerBuiltAlbumFolder()
    {
        var fs = Fs("/dest/Artist/Album A", "/dest/Artist/Album B");
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/Artist/Album A/01.mp3", mbReleaseId: "mbid-a"),
            Song(2, "/dest/Artist/Album A/02.mp3"),
            Song(3, "/dest/Artist/Album B/01.mp3"));
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter();
        await RunSweepAsync(db, fs, writer);

        Assert.Equal(2, writer.Calls.Count);
        // The representative for Album A carries the release MBID even though track 2 lacks it.
        var albumA = writer.Calls.Single(c => c.Folder == "/dest/Artist/Album A");
        Assert.Equal("mbid-a", albumA.Query?.MusicBrainzReleaseId);
    }

    [Fact]
    public async Task ExcludesDemoSyntheticUnreleasedDeletedAndUnbuiltRows()
    {
        var fs = Fs(
            "/dest/A/Demo", "/dest/A/Synthetic", "/dest/A/Unreleased", "/dest/A/Deleted", "/dest/A/Pending");
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/A/Demo/01.mp3", owner: WellKnownUsers.DemoId),
            Song(2, "/dest/A/Synthetic/01.mp3", isSynthetic: true),
            Song(3, "/dest/A/Unreleased/01.mp3", isUnreleased: true),
            Song(4, "/dest/A/Deleted/01.mp3", deleted: true),
            Song(5, "/dest/A/Pending/01.mp3", status: LibraryBuildStatus.Pending));
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter();
        await RunSweepAsync(db, fs, writer);

        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task SkipsFoldersThatAlreadyHaveACoverOnDisk()
    {
        var fs = Fs("/dest/Artist/Album A");
        fs.AddFile("/dest/Artist/Album A/cover.jpg", new MockFileData("art"));
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/dest/Artist/Album A/01.mp3"));
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter();
        await RunSweepAsync(db, fs, writer);

        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task RespectsCooldowns()
    {
        var fs = Fs("/dest/A/Future", "/dest/A/Never", "/dest/A/Due");
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/A/Future/01.mp3"),
            Song(2, "/dest/A/Never/01.mp3"),
            Song(3, "/dest/A/Due/01.mp3"));
        db.AlbumCoverFetchAttempts.AddRange(
            new AlbumCoverFetchAttempt
            {
                AlbumFolder = "/dest/A/Future",
                Status = AlbumCoverFetchStatus.Failed,
                NextRetryAfterUtc = DateTime.UtcNow.AddHours(1),
            },
            new AlbumCoverFetchAttempt
            {
                AlbumFolder = "/dest/A/Never",
                Status = AlbumCoverFetchStatus.NotFound,
                NextRetryAfterUtc = null,
            },
            new AlbumCoverFetchAttempt
            {
                AlbumFolder = "/dest/A/Due",
                Status = AlbumCoverFetchStatus.Failed,
                NextRetryAfterUtc = DateTime.UtcNow.AddHours(-1),
            });
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter();
        await RunSweepAsync(db, fs, writer);

        Assert.Equal(["/dest/A/Due"], writer.Calls.Select(c => c.Folder).ToList());
    }

    [Fact]
    public async Task NotFoundAndTransientFailuresGetDistinctCooldowns()
    {
        var fs = Fs("/dest/A/NotFound", "/dest/A/Transient");
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/A/NotFound/01.mp3"),
            Song(2, "/dest/A/Transient/01.mp3"));
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter
        {
            OnWrite = folder => folder.EndsWith("Transient")
                ? new AlbumCoverWriteResult(false, null, TransientFailure: true)
                : new AlbumCoverWriteResult(false, null),
        };
        await RunSweepAsync(db, fs, writer);

        var notFound = await db.AlbumCoverFetchAttempts.SingleAsync(a => a.AlbumFolder == "/dest/A/NotFound");
        Assert.Equal(AlbumCoverFetchStatus.NotFound, notFound.Status);
        Assert.Equal(1, notFound.AttemptCount);
        Assert.InRange(notFound.NextRetryAfterUtc!.Value, DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));

        var transient = await db.AlbumCoverFetchAttempts.SingleAsync(a => a.AlbumFolder == "/dest/A/Transient");
        Assert.Equal(AlbumCoverFetchStatus.Failed, transient.Status);
        Assert.InRange(transient.NextRetryAfterUtc!.Value, DateTime.UtcNow.AddHours(23.9), DateTime.UtcNow.AddHours(24.1));
    }

    [Fact]
    public async Task NotFoundRetryDisabledLeavesNullNextRetry()
    {
        var fs = Fs("/dest/A/Album");
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/dest/A/Album/01.mp3"));
        await db.SaveChangesAsync();

        await RunSweepAsync(db, fs, new ScriptedCoverWriter(), o => o.ExternalCoverArtNotFoundRetryDays = 0);

        var attempt = await db.AlbumCoverFetchAttempts.SingleAsync();
        Assert.Null(attempt.NextRetryAfterUtc);
    }

    [Fact]
    public async Task SuccessDeletesCooldownAndRecordsSourceTaggedEvent()
    {
        var fs = Fs("/dest/Artist/Album A");
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/dest/Artist/Album A/01.mp3"));
        db.AlbumCoverFetchAttempts.Add(new AlbumCoverFetchAttempt
        {
            AlbumFolder = "/dest/Artist/Album A",
            Status = AlbumCoverFetchStatus.Failed,
            NextRetryAfterUtc = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter
        {
            OnWrite = _ => new AlbumCoverWriteResult(true, "coverartarchive"),
        };
        var written = await RunSweepAsync(db, fs, writer);

        Assert.Equal(1, written);
        Assert.Empty(await db.AlbumCoverFetchAttempts.ToListAsync());
        var evt = await db.LibraryWriteEvents.SingleAsync();
        Assert.Equal(LibraryWriteEventKind.AlbumCoverWritten, evt.Kind);
        Assert.Equal("fetched:coverartarchive", evt.NewValue);
        Assert.Equal("/dest/Artist/Album A", evt.AlbumFolder);
    }

    [Fact]
    public async Task BatchSizeCapsAttemptsPerSweep()
    {
        var fs = Fs("/dest/A/One", "/dest/A/Two", "/dest/A/Three");
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/A/One/01.mp3"),
            Song(2, "/dest/A/Two/01.mp3"),
            Song(3, "/dest/A/Three/01.mp3"));
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter();
        await RunSweepAsync(db, fs, writer, o => o.ExternalCoverArtSweepBatchSize = 1);

        Assert.Single(writer.Calls);
    }

    [Fact]
    public async Task HealsHasCoverArtForFoldersWithACoverOnDisk()
    {
        // A cover written on a prior run (before the flag was populated) leaves the song with a cover
        // on disk but HasCoverArt = false. The sweep's heal pass reflects it back into the flag.
        var fs = Fs("/dest/Artist/Album A");
        fs.AddFile("/dest/Artist/Album A/cover.jpg", new MockFileData("art"));
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/Artist/Album A/01.mp3"),
            Song(2, "/dest/Artist/Album A/02.mp3"));
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter();
        await RunSweepAsync(db, fs, writer);

        // Folder already has a cover, so nothing is fetched — but both tracks get flagged.
        Assert.Empty(writer.Calls);
        Assert.All(await db.Songs.ToListAsync(), s => Assert.True(s.HasCoverArt));
    }

    [Fact]
    public async Task FlagsHasCoverArtWhenACoverIsFetched()
    {
        var fs = Fs("/dest/Artist/Album A");
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/Artist/Album A/01.mp3"),
            Song(2, "/dest/Artist/Album A/02.mp3"));
        await db.SaveChangesAsync();

        var writer = new ScriptedCoverWriter
        {
            OnWrite = _ => new AlbumCoverWriteResult(true, "coverartarchive"),
        };
        var written = await RunSweepAsync(db, fs, writer);

        Assert.Equal(1, written);
        Assert.All(await db.Songs.ToListAsync(), s => Assert.True(s.HasCoverArt));
    }

    [Fact]
    public async Task HealDoesNotFlagDemoSyntheticUnreleasedDeletedOrUnbuiltRows()
    {
        var fs = Fs(
            "/dest/A/Demo", "/dest/A/Synthetic", "/dest/A/Unreleased", "/dest/A/Deleted", "/dest/A/Pending");
        foreach (var dir in new[] { "Demo", "Synthetic", "Unreleased", "Deleted", "Pending" })
        {
            fs.AddFile($"/dest/A/{dir}/cover.jpg", new MockFileData("art"));
        }

        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, "/dest/A/Demo/01.mp3", owner: WellKnownUsers.DemoId),
            Song(2, "/dest/A/Synthetic/01.mp3", isSynthetic: true),
            Song(3, "/dest/A/Unreleased/01.mp3", isUnreleased: true),
            Song(4, "/dest/A/Deleted/01.mp3", deleted: true),
            Song(5, "/dest/A/Pending/01.mp3", status: LibraryBuildStatus.Pending));
        await db.SaveChangesAsync();

        await RunSweepAsync(db, fs, new ScriptedCoverWriter());

        Assert.All(await db.Songs.IgnoreQueryFilters().ToListAsync(), s => Assert.False(s.HasCoverArt));
    }

    private static async Task<int> RunSweepAsync(
        MusicHoarderDbContext db,
        MockFileSystem fs,
        ScriptedCoverWriter writer,
        Action<MusicEnricherOptions>? configure = null)
    {
        var options = new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        };
        configure?.Invoke(options);

        var resolver = new CoverArtResolver(fs, new NoEmbeddedPictureReader());
        var service = new ExternalCoverArtSweepBackgroundService(
            new FixedScopeFactory(db, fs, resolver, writer),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<ExternalCoverArtSweepBackgroundService>.Instance);

        return await service.RunSweepAsync(CancellationToken.None);
    }

    private static MockFileSystem Fs(params string[] directories)
    {
        var fs = new MockFileSystem();
        foreach (var dir in directories)
        {
            fs.AddDirectory(dir);
        }

        return fs;
    }

    private static SongMetadata Song(
        int id,
        string destinationPath,
        Guid? owner = null,
        bool isSynthetic = false,
        bool isUnreleased = false,
        bool deleted = false,
        LibraryBuildStatus status = LibraryBuildStatus.Done,
        string? mbReleaseId = null) => new()
    {
        Id = id,
        OwnerUserId = owner ?? WellKnownUsers.OwnerId,
        SourcePath = $"/source/{id}.mp3",
        FileName = Path.GetFileName(destinationPath),
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = "t",
        Artist = "Artist",
        AlbumArtist = "Artist",
        Album = "Album",
        MusicBrainzReleaseId = mbReleaseId,
        DestinationPath = destinationPath,
        LibraryBuildStatus = status,
        IsSynthetic = isSynthetic,
        IsUnreleased = isUnreleased,
        DeletedAtUtc = deleted ? DateTime.UtcNow : null,
    };

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class NoEmbeddedPictureReader : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => null;
    }

    private sealed class ScriptedCoverWriter : IAlbumCoverWriter
    {
        public Func<string, AlbumCoverWriteResult> OnWrite { get; set; } = _ => new AlbumCoverWriteResult(false, null);
        public List<(string Folder, ExternalCoverArtQuery? Query)> Calls { get; } = [];

        public bool WriteIfMissing(string destinationDirectory, string sourceAudioPath) => false;

        public Task<AlbumCoverWriteResult> WriteIfMissingAsync(
            string destinationDirectory, string sourceAudioPath, ExternalCoverArtQuery? externalQuery, CancellationToken ct = default)
        {
            Calls.Add((destinationDirectory, externalQuery));
            return Task.FromResult(OnWrite(destinationDirectory));
        }
    }

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
