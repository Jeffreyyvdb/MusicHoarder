using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Artwork;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// Regression cover for the album-artist oscillation: the album identity has two writers — the
/// build-time election in <see cref="LibraryBuilderService"/> (which picks the destination folder and
/// the file's tags) and the persisting <see cref="AlbumSplitHealer"/> (which writes the DB) — and both
/// group on <see cref="AlbumGroupKey"/>. While that key kept the collaborator suffix, the two
/// spellings of one album were two groups: each elected its own spelling, the album occupied two
/// artist folders, and a correction that crossed the boundary handed the song to the group that
/// elects the other spelling. On prod that ran forever — "Marvin Gaye" ⇄ "Marvin Gaye &amp; Tammi
/// Terrell", every flip a full re-tag + relocate, pushed to the sync receiver as another copy.
/// </summary>
public class AlbumArtistOscillationTests
{
    [Fact]
    public async Task SplitSpelling_FirstBuildElectsOneFolder_AndBothWritersAgree()
    {
        var fileSystem = SourceFiles(6);
        await using var db = CreateDbContext();
        SeedSplitAlbum(db, LibraryBuildStatus.Pending, destinationPath: null);
        await db.SaveChangesAsync();

        var tagWriter = new RecordingTagWriter();
        var builder = CreateBuilder(db, fileSystem, tagWriter);

        var folders = new List<string>();
        var heals = new List<AlbumSplitHealResult>();
        for (var round = 1; round <= 4; round++)
        {
            heals.Add(await Healer(db).HealAsync());
            await builder.ProcessNextBatchAsync(Guid.NewGuid());
            folders.Add(await DistinctFoldersAsync(db));
        }

        // One logical album -> one election -> one folder, from the very first build, and it never moves.
        Assert.All(folders, f => Assert.Equal(folders[0], f));
        Assert.Equal("/dest/Marvin Gaye/1967 - United", folders[0]);

        // After the album converges nothing is corrected or re-queued again, so no re-copy, no re-tag,
        // and nothing new to push at a sync receiver.
        Assert.All(heals.Skip(1), h => Assert.Equal(new AlbumSplitHealResult(0, 0, 0), h));

        // The two writers agree: the album artist stamped into every file is the one the DB holds.
        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        var albumArtist = Assert.Single(songs.Select(s => s.AlbumArtist).Distinct(StringComparer.Ordinal));
        Assert.All(songs, s => Assert.Equal(albumArtist, tagWriter.IdentityBySource[s.SourcePath].AlbumArtist));
    }

    [Fact]
    public async Task AlreadyBuiltIntoTwoArtistFolders_RelocatesOnceThenStopsChurning()
    {
        // The state prod was actually in: the album already on disk under both spellings.
        var fileSystem = SourceFiles(6);
        await using var db = CreateDbContext();
        SeedSplitAlbum(db, LibraryBuildStatus.Done, destinationPath: "per-spelling");
        await db.SaveChangesAsync();

        foreach (var song in await db.Songs.IgnoreQueryFilters().ToListAsync())
        {
            fileSystem.AddFile(song.DestinationPath!, new MockFileData("audio-bytes"));
        }

        var tagWriter = new RecordingTagWriter();
        var builder = CreateBuilder(db, fileSystem, tagWriter);

        var writesPerRound = new List<int>();
        for (var round = 1; round <= 4; round++)
        {
            await Healer(db).HealAsync();
            var before = tagWriter.Paths.Count;
            await builder.ProcessNextBatchAsync(Guid.NewGuid());
            writesPerRound.Add(tagWriter.Paths.Count - before);
        }

        Assert.Equal("/dest/Marvin Gaye/1967 - United", await DistinctFoldersAsync(db));

        // Exactly one relocate pass — the minority spelling's three tracks — then silence. Before the
        // group key was folded to the lead artist this list never reached zero.
        Assert.Equal(3, writesPerRound[0]);
        Assert.All(writesPerRound.Skip(1), writes => Assert.Equal(0, writes));

        // The abandoned artist folder's files are gone, not left as a second copy of the album.
        Assert.DoesNotContain(fileSystem.AllFiles, f => f.Contains("Tammi Terrell", StringComparison.Ordinal));
    }

    private static async Task<string> DistinctFoldersAsync(MusicHoarderDbContext db)
    {
        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        return string.Join(" | ", songs
            .Select(s => Path.GetDirectoryName(s.DestinationPath) ?? "<none>")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
    }

    private static void SeedSplitAlbum(
        MusicHoarderDbContext db, LibraryBuildStatus buildStatus, string? destinationPath)
    {
        for (var i = 1; i <= 6; i++)
        {
            var albumArtist = i <= 3 ? "Marvin Gaye" : "Marvin Gaye & Tammi Terrell";
            db.Songs.Add(new SongMetadata
            {
                OwnerUserId = WellKnownUsers.OwnerId,
                SourcePath = $"/source/u{i}.flac",
                FileName = $"u{i}.flac",
                Extension = ".flac",
                FileSizeBytes = 11,
                LastModifiedUtc = DateTime.UtcNow,
                IndexedAtUtc = DateTime.UtcNow,
                EnrichmentStatus = EnrichmentStatus.Matched,
                OriginalMetadataCaptured = true,
                Artist = "Marvin Gaye & Tammi Terrell",
                AlbumArtist = albumArtist,
                Album = "United",
                Title = $"Track {i}",
                TrackNumber = i,
                Year = 1967,
                LibraryBuildStatus = buildStatus,
                DestinationPath = destinationPath == "per-spelling"
                    ? $"/dest/{albumArtist}/1967 - United/{i:00} - Track {i}.flac"
                    : destinationPath,
            });
        }
    }

    private static MockFileSystem SourceFiles(int count)
    {
        var files = new Dictionary<string, MockFileData>();
        for (var i = 1; i <= count; i++)
        {
            files[$"/source/u{i}.flac"] = new MockFileData("audio-bytes");
        }

        return new MockFileSystem(files);
    }

    private static MusicEnricherOptions BuildOptions() => new()
    {
        SourceDirectory = "/source",
        DestinationDirectory = "/dest",
        LibraryBuilderBatchSize = 100,
        LibraryBuilderWorkerConcurrency = 1,
        EnableAlbumIdentityReconciliation = true,
        EnableCanonicalDrivenBuild = true,
        LyricsBeforeBuildWaitMinutes = 0,
    };

    private static LibraryBuilderService CreateBuilder(
        MusicHoarderDbContext db, IFileSystem fileSystem, ILibraryTagWriter tagWriter)
    {
        var options = Microsoft.Extensions.Options.Options.Create(BuildOptions());
        var coverWriter = new AlbumCoverWriter(
            fileSystem,
            new CoverArtResolver(fileSystem, new NoPictureReader()),
            new StubExternalCoverArtFetcher(),
            options,
            NullLogger<AlbumCoverWriter>.Instance);

        return new LibraryBuilderService(
            new SingleScopeFactory(db, tagWriter),
            new DestinationPathResolver(options),
            fileSystem,
            new LibraryDestinationCleaner(fileSystem),
            tagWriter,
            coverWriter,
            new AlbumIdentityReconciler(),
            options,
            TestPipelineMetrics.Create(),
            new NoOpTrackSyncEnqueuer(),
            NullLogger<LibraryBuilderService>.Instance);
    }

    private static IAlbumSplitHealer Healer(MusicHoarderDbContext db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(BuildOptions());
        return new AlbumSplitHealer(
            db,
            new AlbumIdentityReconciler(),
            new DestinationPathResolver(options),
            options,
            NullLogger<AlbumSplitHealer>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class RecordingTagWriter : ILibraryTagWriter
    {
        public List<string> Paths { get; } = [];

        public Dictionary<string, AlbumIdentity> IdentityBySource { get; } = new(StringComparer.Ordinal);

        public Task WriteTagsAsync(string path, SongMetadata song, AlbumIdentity albumIdentity, CancellationToken ct = default)
        {
            Paths.Add(path);
            IdentityBySource[song.SourcePath] = albumIdentity;
            return Task.CompletedTask;
        }
    }

    private sealed class NoPictureReader : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => null;
    }

    private sealed class NoOpTrackSyncEnqueuer : MusicHoarder.Api.Sync.ITrackSyncEnqueuer
    {
        public void TryEnqueue(int songId, Guid ownerUserId) { }
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
