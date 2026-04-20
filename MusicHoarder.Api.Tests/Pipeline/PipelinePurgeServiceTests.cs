using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

        var service = CreateService(db, fileSystem);

        var result = await service.ResetPostFingerprintAsync();

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

        var service = CreateService(db, fileSystem);

        var result = await service.ResetPostFingerprintAsync();

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

        var service = CreateService(db, fileSystem);

        var result = await service.PurgeAllAsync();

        Assert.Equal(2, result.SongsAffected);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.SpotifyMatchesCleared);
        Assert.Empty(await db.Songs.ToListAsync());
        Assert.Empty(await db.SongProviderAttempts.ToListAsync());
        Assert.Empty(await db.SpotifyTrackLibraryMatches.ToListAsync());
        Assert.False(fileSystem.File.Exists(destinationPath));
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

        var service = CreateService(db, fileSystem);

        var result = await service.ResetPostFingerprintAsync();

        Assert.Equal(1, result.SongsAffected);
        Assert.Equal(1, result.FilesDeleted);
    }

    private static PipelinePurgeService CreateService(MusicHoarderDbContext db, IFileSystem fileSystem)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        });
        var cleaner = new LibraryDestinationCleaner(fileSystem);
        return new PipelinePurgeService(
            db,
            options,
            cleaner,
            NullLogger<PipelinePurgeService>.Instance);
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
