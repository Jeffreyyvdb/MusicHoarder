using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Auth;

public class MultiTenancyIsolationTests
{
    [Fact]
    public async Task Songs_query_filter_scopes_to_current_user()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // Seed: one song for Owner, one for Demo, on the same DB.
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.AddRange(
                MakeSong(TestUsers.OwnerId, "/owner/song.mp3"),
                MakeSong(TestUsers.DemoId,  "/demo/song.mp3"));
            await seed.SaveChangesAsync();
        }

        // Query as Owner — should see only owner's song.
        await using (var asOwner = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)))
        {
            var visible = await asOwner.Songs.Select(s => s.SourcePath).ToListAsync();
            Assert.Single(visible);
            Assert.Equal("/owner/song.mp3", visible[0]);
        }

        // Query as Demo — should see only demo's song.
        await using (var asDemo = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.DemoUser)))
        {
            var visible = await asDemo.Songs.Select(s => s.SourcePath).ToListAsync();
            Assert.Single(visible);
            Assert.Equal("/demo/song.mp3", visible[0]);
        }

        // Anonymous (no accessor) — sees nothing of either user's data.
        await using (var anon = new MusicHoarderDbContext(options))
        {
            var visible = await anon.Songs.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(2, visible.Count); // bypass returns both
        }
    }

    [Fact]
    public async Task SpotifySettings_query_filter_isolates_per_user()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.SpotifySettings.AddRange(
                new SpotifySettings { OwnerUserId = TestUsers.OwnerId, AccessToken = "owner-token" },
                new SpotifySettings { OwnerUserId = TestUsers.DemoId,  AccessToken = "demo-token"  });
            await seed.SaveChangesAsync();
        }

        await using var asOwner = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));
        var visible = await asOwner.SpotifySettings.Select(s => s.AccessToken).ToListAsync();
        Assert.Single(visible);
        Assert.Equal("owner-token", visible[0]);
    }

    private static SongMetadata MakeSong(Guid ownerId, string sourcePath) => new()
    {
        OwnerUserId = ownerId,
        SourcePath = sourcePath,
        FileSizeBytes = 1,
        FileName = Path.GetFileName(sourcePath),
        Extension = ".mp3",
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
