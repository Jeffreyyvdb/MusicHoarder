using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace MusicHoarder.Api.Tests.Pipeline;

public class PipelinePurgeServiceTests
{
    [Fact]
    public async Task ResetPostFingerprint_ClearsDownstreamStateAndKeepsFingerprints()
    {
        var destinationPath = "/dest/Artist/Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [destinationPath] = new("audio-bytes"),
        });

        await using var db = CreateDbContext();
        var song = CreateEnrichedSong("/src/track.mp3");
        song.MarkBuildDone(destinationPath);
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        db.Songs.Add(song);
        db.SpotifyTrackLibraryMatches.Add(new SpotifyTrackLibraryMatch
        {
            SpotifyTrackId = "spotify-track-1",
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (service, tracker) = CreateService(db, fileSystem);

        var result = await service.ResetPostFingerprintAsync(Guid.NewGuid());

        Assert.Equal(1, result.SongsAffected);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.SpotifyMatchesCleared);

        var reloaded = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync();
        Assert.Equal("fingerprint-abc", reloaded.Fingerprint);
        Assert.Equal(180, reloaded.DurationSeconds);
        Assert.Equal(EnrichmentStatus.Pending, reloaded.EnrichmentStatus);
        Assert.Equal(LibraryBuildStatus.Pending, reloaded.LibraryBuildStatus);
        Assert.Null(reloaded.DestinationPath);
        Assert.Null(reloaded.PreviousDestinationPath);
        Assert.Empty(reloaded.ProviderAttempts);
        Assert.False(fileSystem.File.Exists(destinationPath));
        Assert.Empty(await db.SpotifyTrackLibraryMatches.ToListAsync());

        var snapshot = tracker.Get();
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal("post-fingerprint", snapshot.Mode);
        Assert.Equal(1, snapshot.SongsTotal);
        Assert.Equal(1, snapshot.SongsProcessed);
        Assert.Equal(1, snapshot.FilesTotal);
        Assert.Equal(1, snapshot.FilesDeleted);
        Assert.Equal(0, snapshot.FilesFailed);
        Assert.Equal(1, snapshot.SpotifyMatchesCleared);
    }

    [Fact]
    public async Task ResetPostFingerprint_LeavesSoftDeletedSongsUntouched()
    {
        var fileSystem = new MockFileSystem();

        await using var db = CreateDbContext();
        var activeSong = CreateEnrichedSong("/src/active.mp3");
        var deletedSong = CreateEnrichedSong("/src/deleted.mp3");
        var deletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        deletedSong.DeletedAtUtc = deletedAt;
        db.Songs.AddRange(activeSong, deletedSong);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, fileSystem);

        var result = await service.ResetPostFingerprintAsync(Guid.NewGuid());

        Assert.Equal(1, result.SongsAffected);

        var refreshedDeleted = await db.Songs.SingleAsync(s => s.SourcePath == "/src/deleted.mp3");
        Assert.Equal(deletedAt, refreshedDeleted.DeletedAtUtc);
        Assert.Equal(EnrichmentStatus.Matched, refreshedDeleted.EnrichmentStatus);

        var refreshedActive = await db.Songs.SingleAsync(s => s.SourcePath == "/src/active.mp3");
        Assert.Equal(EnrichmentStatus.Pending, refreshedActive.EnrichmentStatus);
    }

    [Fact]
    public async Task PurgeAll_DeletesSongsProviderAttemptsAndSpotifyMatches()
    {
        var destinationPath = "/dest/Artist/Album/01 - Track.mp3";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [destinationPath] = new("audio-bytes"),
        });

        await using var db = CreateDbContext();
        var song = CreateEnrichedSong("/src/track.mp3");
        song.MarkBuildDone(destinationPath);
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        db.Songs.Add(song);

        var deletedSong = CreateEnrichedSong("/src/deleted.mp3");
        deletedSong.DeletedAtUtc = DateTime.UtcNow;
        db.Songs.Add(deletedSong);

        db.SpotifyTrackLibraryMatches.Add(new SpotifyTrackLibraryMatch
        {
            SpotifyTrackId = "spotify-track-1",
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (service, tracker) = CreateService(db, fileSystem);

        var result = await service.PurgeAllAsync(Guid.NewGuid());

        Assert.Equal(2, result.SongsAffected);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.SpotifyMatchesCleared);
        Assert.Empty(await db.Songs.ToListAsync());
        Assert.Empty(await db.SongProviderAttempts.ToListAsync());
        Assert.Empty(await db.SpotifyTrackLibraryMatches.ToListAsync());
        Assert.False(fileSystem.File.Exists(destinationPath));

        var snapshot = tracker.Get();
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal("all", snapshot.Mode);
        Assert.Equal(2, snapshot.SongsTotal);
    }

    [Fact]
    public async Task ResetPostFingerprint_IgnoresMissingDestinationFile()
    {
        var fileSystem = new MockFileSystem();

        await using var db = CreateDbContext();
        var song = CreateEnrichedSong("/src/track.mp3");
        song.MarkBuildDone("/dest/Artist/Album/01 - Track.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var (service, _) = CreateService(db, fileSystem);

        var result = await service.ResetPostFingerprintAsync(Guid.NewGuid());

        Assert.Equal(1, result.SongsAffected);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(0, result.FilesFailed);
    }

    [Fact]
    public async Task ResetPostFingerprint_CountsUnexpectedFilesystemErrorsAsFailed()
    {
        var destinationPath = "/dest/Artist/Album/01 - Track.mp3";

        await using var db = CreateDbContext();
        var song = CreateEnrichedSong("/src/track.mp3");
        song.MarkBuildDone(destinationPath);
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        });
        var tracker = new PurgeStatusTracker();
        var service = new PipelinePurgeService(
            db,
            options,
            new ThrowingCleaner(),
            new JobManager(),
            tracker,
            NullLogger<PipelinePurgeService>.Instance);

        var result = await service.ResetPostFingerprintAsync(Guid.NewGuid());

        Assert.Equal(1, result.SongsAffected);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(1, result.FilesFailed);

        // DB state is still reset so the next run can re-enrich.
        var reloaded = await db.Songs.SingleAsync();
        Assert.Equal(EnrichmentStatus.Pending, reloaded.EnrichmentStatus);

        var snapshot = tracker.Get();
        Assert.Equal(1, snapshot.FilesFailed);
    }

    [Fact]
    public async Task ResetPostFingerprint_PausesAllStepsAndResumesAfter()
    {
        var fileSystem = new MockFileSystem();
        await using var db = CreateDbContext();
        db.Songs.Add(CreateEnrichedSong("/src/track.mp3"));
        await db.SaveChangesAsync();

        var jobManager = new JobManager();
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        });
        var service = new PipelinePurgeService(
            db,
            options,
            new LibraryDestinationCleaner(fileSystem),
            jobManager,
            new PurgeStatusTracker(),
            NullLogger<PipelinePurgeService>.Instance);

        await service.ResetPostFingerprintAsync(Guid.NewGuid());

        foreach (var step in new[] { JobType.Scan, JobType.Fingerprint, JobType.Enrich, JobType.Build })
        {
            Assert.False(jobManager.IsStepPaused(step), $"{step} should be resumed after purge completes.");
        }
    }

    [Fact]
    public async Task ResetPostFingerprint_DeletesFilesInParallelWithoutLosingCounts()
    {
        // Parallel.ForEachAsync over a real temp directory — MockFileSystem is not thread-safe,
        // and we want to prove prune-races between threads don't corrupt counts or orphan files.
        var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"musichoarder-purge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var filePaths = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                var album = System.IO.Path.Combine(tempRoot, "Artist", $"Album{i / 4}");
                Directory.CreateDirectory(album);
                var file = System.IO.Path.Combine(album, $"{i:00} - Track.mp3");
                await File.WriteAllTextAsync(file, "audio-bytes");
                filePaths.Add(file);
            }

            await using var db = CreateDbContext();
            for (var i = 0; i < filePaths.Count; i++)
            {
                var song = CreateEnrichedSong($"/src/track-{i}.mp3");
                song.MarkBuildDone(filePaths[i]);
                db.Songs.Add(song);
            }
            await db.SaveChangesAsync();

            var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = tempRoot,
            });
            var cleaner = new LibraryDestinationCleaner(new FileSystem());
            var service = new PipelinePurgeService(
                db,
                options,
                cleaner,
                new JobManager(),
                new PurgeStatusTracker(),
                NullLogger<PipelinePurgeService>.Instance);

            var result = await service.ResetPostFingerprintAsync(Guid.NewGuid());

            Assert.Equal(filePaths.Count, result.SongsAffected);
            Assert.Equal(filePaths.Count, result.FilesDeleted);
            Assert.Equal(0, result.FilesFailed);
            foreach (var path in filePaths)
            {
                Assert.False(File.Exists(path), $"{path} should be deleted");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class ThrowingCleaner : ILibraryDestinationCleaner
    {
        public void DeleteManagedPathAndPrune(string path, string destinationRoot)
            => throw new IOException("simulated permission error");
    }

    private static (PipelinePurgeService service, PurgeStatusTracker tracker) CreateService(
        MusicHoarderDbContext db,
        IFileSystem fileSystem)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        });
        var cleaner = new LibraryDestinationCleaner(fileSystem);
        var tracker = new PurgeStatusTracker();
        var service = new PipelinePurgeService(
            db,
            options,
            cleaner,
            new JobManager(),
            tracker,
            NullLogger<PipelinePurgeService>.Instance);
        return (service, tracker);
    }

    private static SongMetadata CreateEnrichedSong(string sourcePath)
    {
        var song = new SongMetadata
        {
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = ".mp3",
            FileSizeBytes = 1234L,
            LastModifiedUtc = new DateTime(2026, 1, 1),
            IndexedAtUtc = new DateTime(2026, 1, 1),
            Fingerprint = "fingerprint-abc",
            DurationSeconds = 180,
            DurationMs = 180_000,
            Bitrate = 320,
            Artist = "Original",
            Title = "Original",
            Album = "Original",
        };
        song.CaptureOriginalMetadata();
        song.ApplyEnrichmentMatch(new EnrichmentMatchData(
            Artist: "New Artist",
            AlbumArtist: "New AlbumArtist",
            Title: "New Title",
            Year: 2024,
            TrackNumber: 1,
            MusicBrainzId: "mb-1",
            MusicBrainzReleaseId: "mbrel-1",
            SpotifyId: "sp-1",
            AcoustIdTrackId: "acid-1",
            Isrc: "ISRC-NEW",
            MatchedBy: "AcoustID",
            AdjustedScore: 0.9,
            WarningsJson: null,
            RecommendedStatus: EnrichmentStatus.Matched,
            Album: "New Album"));
        return song;
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
