using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// The compiled model bakes the query filters in, so two contexts whose filters differ must never share a
/// cached model: whichever built it first would decide which rows every later context can see.
/// </summary>
public class UserAwareModelCacheKeyFactoryTests
{
    private static readonly DbContextOptions Options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    [Fact]
    public void a_context_with_no_accessor_never_shares_a_model_with_one_whose_accessor_has_no_user()
    {
        using var unfiltered = new MusicHoarderDbContext(Options);
        using var signedOut = new MusicHoarderDbContext(Options, new TestCurrentUserAccessor());

        Assert.NotEqual(KeyOf(unfiltered), KeyOf(signedOut));
    }

    [Fact]
    public void contexts_for_different_users_share_one_model_and_each_sees_only_its_own_rows()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.AddRange(Song(TestUsers.OwnerId, "/owner.mp3"), Song(TestUsers.FriendId, "/friend.mp3"));
            seed.SaveChanges();
        }

        using var owner = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));
        using var friend = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser));

        Assert.Same(owner.Model, friend.Model);
        Assert.Equal(["/friend.mp3"], friend.Songs.Select(s => s.SourcePath).ToList());
        Assert.Equal(["/owner.mp3"], owner.Songs.Select(s => s.SourcePath).ToList());
        Assert.Equal(["/friend.mp3"], friend.Songs.Select(s => s.SourcePath).ToList());
    }

    private static SongMetadata Song(Guid ownerId, string path) => new()
    {
        OwnerUserId = ownerId,
        SourcePath = path,
        FileName = Path.GetFileName(path),
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static object KeyOf(DbContext context) => new UserAwareModelCacheKeyFactory().Create(context, designTime: false);
}
