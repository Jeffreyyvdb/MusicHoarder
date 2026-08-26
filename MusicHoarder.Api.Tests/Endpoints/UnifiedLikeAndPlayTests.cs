using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Like and play now serve every account from one handler, so the branch that decides WHERE the
/// write lands is the thing under test: own the row → the row's own columns; do not own it → a
/// <see cref="UserSongState"/> row, and nothing propagates outward.
/// </summary>
public class UnifiedLikeAndPlayTests
{
    [Fact]
    public async Task Member_like_writes_their_own_state_and_never_the_owners_row()
    {
        // L5. Getting this backwards writes a guest's taste into the admin's library.
        var options = NewOptions();
        await Seed(options);

        var navidrome = new RecordingNavidromeEnqueuer();
        var sync = new RecordingTrackSyncEnqueuer();

        await using (var db = Context(options, Member))
        {
            var result = await SongsEndpoints.LikeSong(
                1, db, TestLibraryScope.For(Member), new TestCurrentUserAccessor(Member),
                navidrome, sync, CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        }

        await using var verify = new MusicHoarderDbContext(options);

        var state = Assert.Single(await verify.UserSongStates.ToListAsync());
        Assert.Equal(TestUsers.FriendId, state.UserId);
        Assert.Equal(1, state.SongId);
        Assert.NotNull(state.LikedAtUtc);

        var song = await verify.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == 1);
        Assert.Null(song.LikedAtUtc);
        Assert.Equal(0, song.PlayCount);
    }

    [Fact]
    public async Task Member_like_never_reaches_navidrome_or_instance_sync()
    {
        // L6. These mirror the LIBRARY OWNER's taste to their own servers. A guest's like arriving
        // there would star tracks in someone else's Navidrome that they never liked.
        var options = NewOptions();
        await Seed(options);

        var navidrome = new RecordingNavidromeEnqueuer();
        var sync = new RecordingTrackSyncEnqueuer();

        await using (var db = Context(options, Member))
        {
            await SongsEndpoints.LikeSong(
                1, db, TestLibraryScope.For(Member), new TestCurrentUserAccessor(Member),
                navidrome, sync, CancellationToken.None);
            await SongsEndpoints.ReportPlayed(
                1, db, TestLibraryScope.For(Member), new TestCurrentUserAccessor(Member),
                CancellationToken.None);
        }

        Assert.Empty(navidrome.Calls);
        Assert.Empty(sync.Calls);
    }

    [Fact]
    public async Task Admin_like_still_writes_the_row_and_still_propagates()
    {
        // The other half of the same branch: unification must not have broken the owner's path.
        var options = NewOptions();
        await Seed(options);

        var navidrome = new RecordingNavidromeEnqueuer();
        var sync = new RecordingTrackSyncEnqueuer();

        await using (var db = Context(options, Admin))
        {
            await SongsEndpoints.LikeSong(
                1, db, TestLibraryScope.For(Admin), new TestCurrentUserAccessor(Admin),
                navidrome, sync, CancellationToken.None);
        }

        await using var verify = new MusicHoarderDbContext(options);
        Assert.NotNull((await verify.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == 1)).LikedAtUtc);
        Assert.Empty(await verify.UserSongStates.ToListAsync());

        Assert.Equal((1, TestUsers.OwnerId), Assert.Single(navidrome.Calls));
        Assert.Equal((1, TestUsers.OwnerId), Assert.Single(sync.Calls));
    }

    [Fact]
    public async Task Member_play_counts_are_their_own_and_do_not_move_the_owners_counter()
    {
        var options = NewOptions();
        await Seed(options);

        await using (var db = Context(options, Member))
        {
            var scope = TestLibraryScope.For(Member);
            var accessor = new TestCurrentUserAccessor(Member);
            await SongsEndpoints.ReportPlayed(1, db, scope, accessor, CancellationToken.None);
            await SongsEndpoints.ReportPlayed(1, db, scope, accessor, CancellationToken.None);
        }

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal(2, Assert.Single(await verify.UserSongStates.ToListAsync()).PlayCount);
        Assert.Equal(0, (await verify.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == 1)).PlayCount);
    }

    [Fact]
    public async Task Member_unlike_clears_only_their_own_state()
    {
        var options = NewOptions();
        await Seed(options);

        await using (var seed = new MusicHoarderDbContext(options))
        {
            // The admin likes it too — the member unliking must not clear that.
            (await seed.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == 1)).LikedAtUtc = DateTime.UtcNow;
            seed.UserSongStates.Add(new UserSongState
            {
                UserId = TestUsers.FriendId,
                SongId = 1,
                LikedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = Context(options, Member))
        {
            await SongsEndpoints.UnlikeSong(
                1, db, TestLibraryScope.For(Member), new TestCurrentUserAccessor(Member),
                new RecordingNavidromeEnqueuer(), new RecordingTrackSyncEnqueuer(), CancellationToken.None);
        }

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Null(Assert.Single(await verify.UserSongStates.ToListAsync()).LikedAtUtc);
        Assert.NotNull((await verify.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == 1)).LikedAtUtc);
    }

    [Fact]
    public async Task Liking_an_ungranted_song_404s_and_writes_nothing()
    {
        var options = NewOptions();
        await Seed(options, alsoSeedUngranted: true);

        await using (var db = Context(options, Member))
        {
            var result = await SongsEndpoints.LikeSong(
                2, db, TestLibraryScope.For(Member), new TestCurrentUserAccessor(Member),
                new RecordingNavidromeEnqueuer(), new RecordingTrackSyncEnqueuer(), CancellationToken.None);
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        }

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Empty(await verify.UserSongStates.ToListAsync());
    }

    // --- helpers -----------------------------------------------------------------------------

    private static CurrentUser Admin =>
        new(TestUsers.OwnerId, "admin@test.local", UserRole.Admin, "Admin");

    private static CurrentUser Member =>
        new(TestUsers.FriendId, "member@test.local", UserRole.Member, "Member", Capability.TrackListening);

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static MusicHoarderDbContext Context(
        DbContextOptions<MusicHoarderDbContext> options, CurrentUser caller) =>
        new(options, new TestCurrentUserAccessor(caller));

    private static int? StatusOf(IResult result) =>
        result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    private static async Task Seed(
        DbContextOptions<MusicHoarderDbContext> options, bool alsoSeedUngranted = false)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.Songs.Add(Song(1, "Daft Punk", "Discovery", "One More Time"));
        if (alsoSeedUngranted)
            db.Songs.Add(Song(2, "Justice", "Cross", "D.A.N.C.E."));

        db.LibraryShareGrants.Add(new LibraryShareGrant
        {
            OwnerUserId = TestUsers.OwnerId,
            GranteeUserId = TestUsers.FriendId,
            Scope = ShareGrantScope.Album,
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static SongMetadata Song(int id, string artist, string album, string title) => new()
    {
        Id = id,
        OwnerUserId = TestUsers.OwnerId,
        SourcePath = $"/music/{id}.mp3",
        FileName = $"{title}.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        Artist = artist,
        Album = album,
        Title = title,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
