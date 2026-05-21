using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class ManualLockAndCooldownTests
{
    [Fact]
    public void ResetEnrichment_LockedSong_IsNoOp_UnlessForced()
    {
        var song = NewSong();
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.MatchedBy = "SpotifyAPI";
        song.LockManualApproval();

        song.ResetEnrichment(); // not forced

        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.Equal("SpotifyAPI", song.MatchedBy);
        Assert.True(song.IsManuallyApproved);
    }

    [Fact]
    public void ResetEnrichment_Forced_ClearsLockAndResets()
    {
        var song = NewSong();
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.MatchedBy = "SpotifyAPI";
        song.LockManualApproval();

        song.ResetEnrichment(restoreOriginal: true, force: true);

        Assert.Equal(EnrichmentStatus.Pending, song.EnrichmentStatus);
        Assert.Null(song.MatchedBy);
        Assert.False(song.IsManuallyApproved);
    }

    [Fact]
    public async Task RetryQuery_IncludesCooldownExpiredNoMatch()
    {
        await using var db = CreateDb();
        var song = NewSong();
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        db.SongProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow.AddDays(-31),
            NextRetryAfterUtc = DateTime.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var ids = await db.SongProviderAttempts
            .WhereRetryableProviderAttempts(DateTime.UtcNow)
            .ToListAsync();

        Assert.Contains(song.Id, ids);
    }

    [Fact]
    public async Task RetryQuery_ExcludesLockedSongs()
    {
        await using var db = CreateDb();
        var song = NewSong();
        song.LockManualApproval();
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        db.SongProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow.AddDays(-31),
            NextRetryAfterUtc = DateTime.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var ids = await db.SongProviderAttempts
            .WhereRetryableProviderAttempts(DateTime.UtcNow)
            .ToListAsync();

        Assert.DoesNotContain(song.Id, ids);
    }

    [Fact]
    public async Task RetryQuery_ExcludesCooldownNotYetElapsed()
    {
        await using var db = CreateDb();
        var song = NewSong();
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        db.SongProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
            NextRetryAfterUtc = DateTime.UtcNow.AddDays(29),
        });
        await db.SaveChangesAsync();

        var ids = await db.SongProviderAttempts
            .WhereRetryableProviderAttempts(DateTime.UtcNow)
            .ToListAsync();

        Assert.DoesNotContain(song.Id, ids);
    }

    private static SongMetadata NewSong() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = $"/source/{Guid.NewGuid():N}.mp3",
        FileName = "song.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Artist",
        Title = "Title",
        EnrichmentStatus = EnrichmentStatus.NeedsReview,
    };

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
