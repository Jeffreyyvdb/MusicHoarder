using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

public class DemoTenantQueryExtensionsTests
{
    [Fact]
    public async Task ExcludingDemoTenant_DropsDemoSongs_KeepsOwnerSongs()
    {
        await using var db = NewDb();
        db.Songs.Add(Song(WellKnownUsers.OwnerId, "/owner/a.mp3"));
        db.Songs.Add(Song(WellKnownUsers.DemoId, "/demo/b.mp3"));
        await db.SaveChangesAsync();

        var kept = await db.Songs.IgnoreQueryFilters().ExcludingDemoTenant().ToListAsync();

        var song = Assert.Single(kept);
        Assert.Equal(WellKnownUsers.OwnerId, song.OwnerUserId);
    }

    [Fact]
    public async Task ExcludingDemoTenant_DropsDemoWishlistItems_KeepsOwnerItems()
    {
        await using var db = NewDb();
        db.WishlistItems.Add(WishlistItem(WellKnownUsers.OwnerId, "owner-track"));
        db.WishlistItems.Add(WishlistItem(WellKnownUsers.DemoId, "demo-track"));
        await db.SaveChangesAsync();

        var kept = await db.WishlistItems.IgnoreQueryFilters().ExcludingDemoTenant().ToListAsync();

        var item = Assert.Single(kept);
        Assert.Equal(WellKnownUsers.OwnerId, item.OwnerUserId);
    }

    [Fact]
    public async Task ExcludingDemoTenant_DropsDemoProviderAttempts_KeepsOwnerAttempts()
    {
        await using var db = NewDb();
        var ownerSong = Song(WellKnownUsers.OwnerId, "/owner/a.mp3");
        var demoSong = Song(WellKnownUsers.DemoId, "/demo/b.mp3");
        ownerSong.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        demoSong.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        db.Songs.AddRange(ownerSong, demoSong);
        await db.SaveChangesAsync();

        var kept = await db.SongProviderAttempts.IgnoreQueryFilters().ExcludingDemoTenant().ToListAsync();

        var attempt = Assert.Single(kept);
        Assert.Equal(ownerSong.Id, attempt.SongId);
    }

    private static SongMetadata Song(Guid owner, string path) => new()
    {
        OwnerUserId = owner,
        SourcePath = path,
        FileName = System.IO.Path.GetFileName(path),
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static WishlistItem WishlistItem(Guid owner, string trackId) => new()
    {
        OwnerUserId = owner,
        SpotifyTrackId = trackId,
        Title = "Title",
        Artist = "Artist",
        Status = WishlistItemStatus.Pending,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static MusicHoarderDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
