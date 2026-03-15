using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;

namespace MusicHoarder.Api.Tests.Library;

public class LibraryBuilderServiceTests
{
    [Fact]
    public async Task ProcessNextBatchAsync_SkipsCopy_WhenDestinationExistsWithMatchingSize()
    {
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("12345"),
            [destinationPath] = new("12345")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Null(song.LibraryBuildError);
        Assert.Empty(tagWriter.Paths);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_UsesTempFileAndRenamesOnSuccess()
    {
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.True(fileSystem.File.Exists(destinationPath));
        var usedTempPath = tagWriter.Paths.Single();
        Assert.Contains(".tmp.", usedTempPath, StringComparison.Ordinal);
        Assert.False(fileSystem.File.Exists(usedTempPath));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_CreatesDestinationDirectory()
    {
        var sourcePath = "/source/track.mp3";
        var destinationDirectory = "/dest/Artist/2026 - Album";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.True(fileSystem.Directory.Exists(destinationDirectory));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ContinuesWhenSingleFileFails()
    {
        var goodSource = "/source/good.mp3";
        var missingSource = "/source/missing.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [goodSource] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(goodSource, 5, "Good Track"));
        db.Songs.Add(CreateMatchedSong(missingSource, 5, "Missing Track"));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var songs = await db.Songs.OrderBy(s => s.Title).ToListAsync();

        Assert.Equal(1, result.Done);
        Assert.Equal(1, result.Failed);
        Assert.Equal(LibraryBuildStatus.Done, songs[0].LibraryBuildStatus);
        Assert.Equal(LibraryBuildStatus.Failed, songs[1].LibraryBuildStatus);
        Assert.False(string.IsNullOrWhiteSpace(songs[1].LibraryBuildError));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ReconcilesLegacyArtistPath_AndPrunesEmptyDirectories()
    {
        var sourcePath = "/source/track.mp3";
        var legacyPath = "/dest/Artist A; Artist B/2026 - Album/01 - Track.mp3";
        var newPath = "/dest/Artist A/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [legacyPath] = new("abcde")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(
            sourcePath,
            5,
            title: "Track",
            artist: "Artist A; Artist B",
            albumArtist: "Artist A",
            libraryBuildStatus: LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Done);
        Assert.True(fileSystem.File.Exists(newPath));
        Assert.False(fileSystem.File.Exists(legacyPath));
        Assert.Equal(newPath, song.DestinationPath);
        Assert.Null(song.PreviousDestinationPath);
        Assert.False(fileSystem.Directory.Exists("/dest/Artist A; Artist B/2026 - Album"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_OverwritesDestination_WhenExistingContentDiffers()
    {
        var sourcePath = "/source/track.mp3";
        var destinationPath = "/dest/Artist/2026 - Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("12345"),
            [destinationPath] = new("different")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();
        var writtenSize = fileSystem.FileInfo.New(destinationPath).Length;

        Assert.Equal(1, result.Done);
        Assert.Equal(5, writtenSize);
        Assert.Equal(destinationPath, song.DestinationPath);
    }

    [Fact]
    public async Task ProcessNextBatchAsync_PreservesUnknownFiles_WhenCleaningLegacyDirectories()
    {
        var sourcePath = "/source/track.mp3";
        var legacyPath = "/dest/Artist A; Artist B/2026 - Album/01 - Track.mp3";
        var unknownPath = "/dest/Artist A; Artist B/2026 - Album/notes.txt";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde"),
            [legacyPath] = new("abcde"),
            [unknownPath] = new("keep me")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(
            sourcePath,
            5,
            title: "Track",
            artist: "Artist A; Artist B",
            albumArtist: "Artist A",
            libraryBuildStatus: LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        var service = CreateService(db, fileSystem, new RecordingTagWriter());

        await service.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.True(fileSystem.File.Exists(unknownPath));
        Assert.True(fileSystem.Directory.Exists("/dest/Artist A; Artist B/2026 - Album"));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_MarksFailed_WhenTempCleanupDeleteThrows()
    {
        var sourcePath = "/source/track.mp3";
        var innerFileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath] = new("abcde")
        });
        var fileSystem = new DeleteThrowingFileSystem(
            innerFileSystem,
            path => path.Contains(".tmp.", StringComparison.Ordinal));

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath, 5));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingThrowingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var song = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Done);
        Assert.Equal(1, result.Failed);
        Assert.Equal(LibraryBuildStatus.Failed, song.LibraryBuildStatus);
        Assert.False(string.IsNullOrWhiteSpace(song.LibraryBuildError));
        Assert.Contains("tag writer exploded", song.LibraryBuildError, StringComparison.OrdinalIgnoreCase);
        var tempPath = Assert.Single(tagWriter.Paths);
        Assert.True(fileSystem.File.Exists(tempPath));
    }

    [Fact]
    public async Task ProcessNextBatchAsync_ProcessesSingleCandidate_WhenDestinationCollidesWithinBatch()
    {
        var sourcePath1 = "/source/track1.mp3";
        var sourcePath2 = "/source/track2.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [sourcePath1] = new("abcde"),
            [sourcePath2] = new("fghij")
        });

        await using var db = CreateDbContext();
        db.Songs.Add(CreateMatchedSong(sourcePath1, 5, title: "Unknown Title", artist: "Artist", albumArtist: "Artist"));
        db.Songs.Add(CreateMatchedSong(sourcePath2, 5, title: "Unknown Title", artist: "Artist", albumArtist: "Artist"));
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var service = CreateService(db, fileSystem, tagWriter);

        var result = await service.ProcessNextBatchAsync(Guid.NewGuid());
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();

        Assert.Equal(1, result.TotalTracks);
        Assert.Equal(1, result.Done);
        Assert.Equal(0, result.Failed);
        Assert.Equal(LibraryBuildStatus.Done, songs[0].LibraryBuildStatus);
        Assert.Equal(LibraryBuildStatus.Pending, songs[1].LibraryBuildStatus);
        Assert.Single(tagWriter.Paths);
    }

    private static LibraryBuilderService CreateService(
        MusicHoarderDbContext db,
        IFileSystem fileSystem,
        ILibraryTagWriter tagWriter)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            LibraryBuilderBatchSize = 100,
            LibraryBuilderWorkerConcurrency = 1,
            LibraryBuilderIdleDelaySeconds = 20
        });

        var resolver = new DestinationPathResolver(options);
        var scopeFactory = new SingleScopeFactory(db, tagWriter);

        return new LibraryBuilderService(
            scopeFactory,
            resolver,
            fileSystem,
            tagWriter,
            options,
            NullLogger<LibraryBuilderService>.Instance);
    }

    private static SongMetadata CreateMatchedSong(
        string sourcePath,
        long size,
        string title = "Track",
        string artist = "Artist",
        string albumArtist = "Artist",
        LibraryBuildStatus libraryBuildStatus = LibraryBuildStatus.Pending)
    {
        return new SongMetadata
        {
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = ".mp3",
            FileSizeBytes = size,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = "Album",
            Title = title,
            Year = 2026,
            TrackNumber = 1,
            EnrichmentStatus = EnrichmentStatus.Matched,
            LibraryBuildStatus = libraryBuildStatus
        };
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class RecordingTagWriter : ILibraryTagWriter
    {
        public List<string> Paths { get; } = [];

        public Task WriteTagsAsync(string path, SongMetadata song, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingThrowingTagWriter : ILibraryTagWriter
    {
        public List<string> Paths { get; } = [];

        public Task WriteTagsAsync(string path, SongMetadata song, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(path);
            throw new IOException("tag writer exploded");
        }
    }

    private sealed class DeleteThrowingFileSystem : IFileSystem
    {
        private readonly IFileSystem inner;

        public DeleteThrowingFileSystem(IFileSystem inner, Func<string, bool> shouldThrowDelete)
        {
            this.inner = inner;
            var proxy = DispatchProxy.Create<IFile, DeleteThrowingFileProxy>();
            var typedProxy = (DeleteThrowingFileProxy)(object)proxy;
            typedProxy.Inner = inner.File;
            typedProxy.ShouldThrowDelete = shouldThrowDelete;
            File = proxy;
        }

        public IDirectory Directory => inner.Directory;
        public IFile File { get; }
        public IFileInfoFactory FileInfo => inner.FileInfo;
        public IFileVersionInfoFactory FileVersionInfo => inner.FileVersionInfo;
        public IPath Path => inner.Path;
        public IDirectoryInfoFactory DirectoryInfo => inner.DirectoryInfo;
        public IDriveInfoFactory DriveInfo => inner.DriveInfo;
        public IFileStreamFactory FileStream => inner.FileStream;
        public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;
    }

    private class DeleteThrowingFileProxy : DispatchProxy
    {
        public IFile Inner { get; set; } = default!;
        public Func<string, bool> ShouldThrowDelete { get; set; } = _ => false;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Proxy method metadata is missing.");
            }

            if (targetMethod.Name == nameof(IFile.Delete)
                && args is [{ } firstArg, ..]
                && firstArg is string path
                && ShouldThrowDelete(path))
            {
                throw new IOException($"Resource busy : '{path}'");
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }

    private sealed class SingleScopeFactory(MusicHoarderDbContext db, ILibraryTagWriter tagWriter)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SingleScope(new SingleScopeProvider(db, tagWriter));
    }

    private sealed class SingleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;

        public void Dispose()
        {
        }
    }

    private sealed class SingleScopeProvider(MusicHoarderDbContext db, ILibraryTagWriter tagWriter) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            if (serviceType == typeof(ILibraryTagWriter)) return tagWriter;
            return null;
        }
    }
}