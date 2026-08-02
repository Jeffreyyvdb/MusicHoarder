using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class DuplicatesEndpointsTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task Resolve_PinsKeeper_FlagsLosers()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        db.Songs.AddRange(
            CreateSong(1, "/a/track.flac"),
            CreateSong(2, "/b/track.mp3"),
            CreateSong(3, "/c/track.mp3"));
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Resolve(
            new DuplicateResolveRequest(KeeperId: 2, LoserIds: [1, 3]), db, CancellationToken.None);

        AssertStatus(result, 200);
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.True(songs[0].IsDuplicate);
        Assert.Equal(2, songs[0].DuplicateOfId);
        Assert.False(songs[1].IsDuplicate);
        Assert.NotNull(songs[1].DuplicateKeeperPinnedAtUtc);
        Assert.True(songs[2].IsDuplicate);
        Assert.Equal(2, songs[2].DuplicateOfId);
    }

    [Fact]
    public async Task Resolve_KeeperInLosers_IsBadRequest()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        db.Songs.AddRange(CreateSong(1, "/a/track.flac"), CreateSong(2, "/b/track.mp3"));
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Resolve(
            new DuplicateResolveRequest(KeeperId: 1, LoserIds: [1, 2]), db, CancellationToken.None);

        AssertStatus(result, 400);
    }

    [Fact]
    public async Task Resolve_ForeignSong_IsNotFound()
    {
        // The per-user query filter must hide another owner's song — resolving against it 404s.
        var options = CreateOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(CreateSong(1, "/a/track.flac", owner: Guid.NewGuid()));
            seed.Songs.Add(CreateSong(2, "/b/track.mp3", owner: Owner));
            await seed.SaveChangesAsync();
        }

        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var result = await DuplicatesEndpoints.Resolve(
            new DuplicateResolveRequest(KeeperId: 2, LoserIds: [1]), db, CancellationToken.None);

        AssertStatus(result, 404);
    }

    [Fact]
    public async Task Dismiss_CreatesDismissedLinks_AndClearsInSetFlags()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));
        var a = CreateSong(1, "/a/track.flac");
        var b = CreateSong(2, "/b/track.mp3");
        b.MarkAsDuplicate(1);
        var c = CreateSong(3, "/c/other.mp3");
        c.MarkAsDuplicate(99); // flagged against a song OUTSIDE the dismissed set — must keep its flag
        db.Songs.AddRange(a, b, c);
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = Owner,
            SongIdLow = 1,
            SongIdHigh = 2,
            Status = DuplicateLinkStatus.Active,
            Confidence = DuplicateConfidence.Confirmed,
            Reasons = DuplicateMatchReason.ExactFingerprint,
            DetectedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await DuplicatesEndpoints.Dismiss(
            new DuplicateDismissRequest([1, 2, 3]), db, new StubCurrentUser(Owner), CancellationToken.None);

        AssertStatus(result, 200);

        // All 3 pairs among the set are dismissed: the existing link flipped, the other two created.
        var links = await db.SongDuplicateLinks.OrderBy(l => l.SongIdLow).ThenBy(l => l.SongIdHigh).ToListAsync();
        Assert.Equal(3, links.Count);
        Assert.All(links, l => Assert.Equal(DuplicateLinkStatus.Dismissed, l.Status));
        Assert.All(links, l => Assert.NotNull(l.DismissedAtUtc));

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.False(songs[1].IsDuplicate); // was flagged against song 1 (in set) — cleared
        Assert.True(songs[2].IsDuplicate);  // flagged against 99 (outside set) — kept
    }

    [Fact]
    public async Task Dismiss_FewerThanTwoIds_IsBadRequest()
    {
        var options = CreateOptions();
        await using var db = new MusicHoarderDbContext(options, new StubCurrentUser(Owner));

        var result = await DuplicatesEndpoints.Dismiss(
            new DuplicateDismissRequest([1]), db, new StubCurrentUser(Owner), CancellationToken.None);

        AssertStatus(result, 400);
    }

    private static void AssertStatus(Microsoft.AspNetCore.Http.IResult result, int expected)
    {
        var status = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(expected, status.StatusCode);
    }

    private static DbContextOptions CreateOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static SongMetadata CreateSong(int id, string sourcePath, Guid? owner = null) => new()
    {
        OwnerUserId = owner ?? Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1_000_000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Test Artist",
        Title = "Test Track",
    };

    private sealed class StubCurrentUser(Guid userId) : ICurrentUserAccessor
    {
        public CurrentUser? User { get; } = new(userId, "owner@test", UserRole.Owner, "Owner");
        public Guid UserId => userId;
    }
}
