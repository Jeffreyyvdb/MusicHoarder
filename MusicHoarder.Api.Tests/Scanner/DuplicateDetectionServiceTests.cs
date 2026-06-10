using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

public class DuplicateDetectionServiceTests
{
    [Fact]
    public async Task DetectDuplicates_FlagsLowerQualityVersions_KeepsBest()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "FP_A", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track.mp3", ".mp3", "FP_A", bitrate: 320, size: 10_000_000),
            CreateSong(3, "/c/track.mp3", ".mp3", "FP_A", bitrate: 128, size: 4_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(1, result.GroupsFound);
        Assert.Equal(2, result.DuplicatesFlagged);
        Assert.False(songs[0].IsDuplicate);
        Assert.Null(songs[0].DuplicateOfId);
        Assert.True(songs[1].IsDuplicate);
        Assert.Equal(1, songs[1].DuplicateOfId);
        Assert.True(songs[2].IsDuplicate);
        Assert.Equal(1, songs[2].DuplicateOfId);
    }

    [Fact]
    public async Task DetectDuplicates_PrefersMp3HighBitrate_OverLowBitrate()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track128.mp3", ".mp3", "FP_B", bitrate: 128, size: 4_000_000),
            CreateSong(2, "/b/track320.mp3", ".mp3", "FP_B", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
        Assert.Null(songs[1].DuplicateOfId);
    }

    [Fact]
    public async Task DetectDuplicates_NoGroupsWithSingleTrack()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.flac", ".flac", "FP_C", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track2.mp3", ".mp3", "FP_D", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(0, result.DuplicatesFlagged);

        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.False(s.IsDuplicate));
    }

    [Fact]
    public async Task DetectDuplicates_IgnoresDeletedSongs()
    {
        await using var db = CreateDbContext();
        var song1 = CreateSong(1, "/a/track.flac", ".flac", "FP_E", bitrate: null, size: 50_000_000);
        var song2 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_E", bitrate: 320, size: 10_000_000);
        song2.SoftDelete();
        db.Songs.AddRange(song1, song2);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
        Assert.False(song1.IsDuplicate);
    }

    [Fact]
    public async Task DetectDuplicates_IgnoresNullFingerprints()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.mp3", ".mp3", null, bitrate: 320, size: 10_000_000),
            CreateSong(2, "/b/track2.mp3", ".mp3", null, bitrate: 128, size: 4_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
    }

    [Fact]
    public async Task DetectDuplicates_IgnoresEmptyFingerprints()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.mp3", ".mp3", "", bitrate: 320, size: 10_000_000),
            CreateSong(2, "/b/track2.mp3", ".mp3", "", bitrate: 128, size: 4_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(0, result.GroupsFound);
    }

    [Fact]
    public async Task DetectDuplicates_ClearsPreviousDuplicateFlags_WhenSourceFileRemoved()
    {
        await using var db = CreateDbContext();
        var song1 = CreateSong(1, "/a/track.flac", ".flac", "FP_F", bitrate: null, size: 50_000_000);
        var song2 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_F", bitrate: 320, size: 10_000_000);
        song2.MarkAsDuplicate(1);
        db.Songs.AddRange(song1, song2);
        await db.SaveChangesAsync();

        song1.Fingerprint = "FP_NEW";
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(0, result.GroupsFound);
        Assert.Equal(1, result.DuplicatesCleared);
        Assert.False(songs[0].IsDuplicate);
        Assert.False(songs[1].IsDuplicate);
        Assert.Null(songs[1].DuplicateOfId);
    }

    [Fact]
    public async Task DetectDuplicates_TiesBreakByFileSize_ThenById()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track_small.mp3", ".mp3", "FP_G", bitrate: 320, size: 9_000_000),
            CreateSong(2, "/b/track_large.mp3", ".mp3", "FP_G", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
    }

    [Fact]
    public async Task DetectDuplicates_IdempotentOnRerun()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac", ".flac", "FP_H", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track.mp3", ".mp3", "FP_H", bitrate: 320, size: 10_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result1 = await service.DetectDuplicatesAsync();
        Assert.Equal(1, result1.DuplicatesFlagged);

        var result2 = await service.DetectDuplicatesAsync();
        Assert.Equal(0, result2.DuplicatesFlagged);
        Assert.Equal(0, result2.DuplicatesCleared);
    }

    [Fact]
    public async Task DetectDuplicates_HandlesMultipleGroups()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track1.flac", ".flac", "FP_I", bitrate: null, size: 50_000_000),
            CreateSong(2, "/b/track1.mp3", ".mp3", "FP_I", bitrate: 320, size: 10_000_000),
            CreateSong(3, "/c/track2.flac", ".flac", "FP_J", bitrate: null, size: 40_000_000),
            CreateSong(4, "/d/track2.mp3", ".mp3", "FP_J", bitrate: 128, size: 4_000_000),
            CreateSong(5, "/e/unique.flac", ".flac", "FP_K", bitrate: null, size: 30_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.DetectDuplicatesAsync();

        Assert.Equal(2, result.GroupsFound);
        Assert.Equal(2, result.DuplicatesFlagged);

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.False(songs[0].IsDuplicate); // FP_I best (FLAC)
        Assert.True(songs[1].IsDuplicate);  // FP_I dup (MP3 320)
        Assert.False(songs[2].IsDuplicate); // FP_J best (FLAC)
        Assert.True(songs[3].IsDuplicate);  // FP_J dup (MP3 128)
        Assert.False(songs[4].IsDuplicate); // unique
    }

    [Fact]
    public async Task DetectDuplicates_PrefersFlacOverAllMp3Bitrates()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            CreateSong(1, "/a/track.mp3", ".mp3", "FP_L", bitrate: 320, size: 10_000_000),
            CreateSong(2, "/b/track.flac", ".flac", "FP_L", bitrate: null, size: 50_000_000));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
    }

    [Fact]
    public void QualityScore_Flac_Returns1000()
    {
        var song = CreateSong(1, "/a/track.flac", ".flac", "FP", bitrate: null, size: 50_000_000);
        Assert.Equal(1000, IDuplicateDetectionService.QualityScore(song));
    }

    [Fact]
    public void QualityScore_Mp3_ReturnsBitrate()
    {
        var song = CreateSong(1, "/a/track.mp3", ".mp3", "FP", bitrate: 320, size: 10_000_000);
        Assert.Equal(320, IDuplicateDetectionService.QualityScore(song));
    }

    [Fact]
    public void QualityScore_Mp3NullBitrate_ReturnsZero()
    {
        var song = CreateSong(1, "/a/track.mp3", ".mp3", "FP", bitrate: null, size: 10_000_000);
        Assert.Equal(0, IDuplicateDetectionService.QualityScore(song));
    }

    [Fact]
    public void QualityScore_Wav_Returns900()
    {
        var song = CreateSong(1, "/a/track.wav", ".wav", "FP", bitrate: null, size: 100_000_000);
        Assert.Equal(900, IDuplicateDetectionService.QualityScore(song));
    }

    [Fact]
    public void QualityScore_UnknownExtension_ReturnsZero()
    {
        var song = CreateSong(1, "/a/track.xyz", ".xyz", "FP", bitrate: null, size: 10_000_000);
        Assert.Equal(0, IDuplicateDetectionService.QualityScore(song));
    }

    [Fact]
    public async Task DetectDuplicates_LibraryBuilderSkipsDuplicates()
    {
        await using var db = CreateDbContext();
        var flac = CreateSong(1, "/a/track.flac", ".flac", "FP_M", bitrate: null, size: 50_000_000);
        flac.EnrichmentStatus = EnrichmentStatus.Matched;
        flac.LyricsStatus = LyricsStatus.Fetched;
        var mp3 = CreateSong(2, "/b/track.mp3", ".mp3", "FP_M", bitrate: 320, size: 10_000_000);
        mp3.EnrichmentStatus = EnrichmentStatus.Matched;
        mp3.LyricsStatus = LyricsStatus.Fetched;
        db.Songs.AddRange(flac, mp3);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DetectDuplicatesAsync();

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();

        Assert.True(songs[0].IsReadyForBuild);
        Assert.False(songs[1].IsReadyForBuild);
    }

    private static SongMetadata CreateSong(
        int id,
        string sourcePath,
        string extension,
        string? fingerprint,
        int? bitrate,
        long size)
    {
        return new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = extension,
            FileSizeBytes = size,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Fingerprint = fingerprint,
            Bitrate = bitrate,
            Artist = "Test Artist",
            Title = "Test Track"
        };
    }

    private static DuplicateDetectionService CreateService(MusicHoarderDbContext db)
    {
        var scopeFactory = new TestScopeFactory(db);
        return new DuplicateDetectionService(
            scopeFactory,
            NullLogger<DuplicateDetectionService>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class TestScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(db);
    }

    private sealed class TestScope(MusicHoarderDbContext db) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new TestServiceProvider(db);
        public void Dispose() { }
    }

    private sealed class TestServiceProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            return null;
        }
    }
}
