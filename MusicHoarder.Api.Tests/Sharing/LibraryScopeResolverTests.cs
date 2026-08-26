using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Sharing;

/// <summary>
/// <see cref="ILibraryScopeResolver"/> is the single door every unified endpoint reads through, so
/// these tests are the isolation guarantee for the whole API. They pin, in order: an account only
/// ever sees its own rows plus explicitly granted ones; a member owns nothing and therefore leaks
/// nothing; revocation takes effect on the very next request; and the slice a song came from is
/// reported correctly, because every like/play write branches on it.
/// </summary>
public class LibraryScopeResolverTests
{
    private static readonly Guid SecondAdminId = new("22222222-2222-2222-2222-222222222222");

    // --- Self slice -------------------------------------------------------------------------

    [Fact]
    public async Task Admin_with_no_grants_sees_exactly_their_own_songs()
    {
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "A", "X", "mine"),
            Song(2, SecondAdminId, "A", "X", "someone elses"));

        var ids = await VisibleIds(options, TestUsers.OwnerId);

        Assert.Equal([1], ids);
    }

    [Fact]
    public async Task One_admin_never_sees_another_admins_library()
    {
        // L4. If the self slice is ever "optimized" into IgnoreQueryFilters() with a hand-written
        // predicate, this is the test that catches it.
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "A", "X", "mine"),
            Song(2, SecondAdminId, "B", "Y", "theirs"),
            Song(3, SecondAdminId, "C", "Z", "also theirs"));

        Assert.Equal([1], await VisibleIds(options, TestUsers.OwnerId));
        Assert.Equal([2, 3], await VisibleIds(options, SecondAdminId));
    }

    [Fact]
    public async Task Member_with_no_grants_sees_nothing()
    {
        // The structural safety property: a member owns zero song rows, so the self slice is empty
        // by construction rather than by a role check.
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));

        Assert.Empty(await VisibleIds(options, TestUsers.FriendId));
    }

    [Fact]
    public async Task Self_slice_is_always_present_and_first()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library, granteeId: TestUsers.FriendId);

        var scope = await Resolve(options, TestUsers.FriendId);

        Assert.True(scope.Slices[0].IsSelf);
        Assert.Equal(TestUsers.FriendId, scope.Slices[0].GrantorUserId);
        Assert.Single(scope.GrantedSlices);
    }

    [Fact]
    public async Task Anonymous_caller_gets_only_an_empty_self_slice()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library, granteeId: TestUsers.FriendId);

        var scope = await Resolve(options, Guid.Empty);

        Assert.Empty(scope.GrantedSlices);
    }

    // --- Granted slices ---------------------------------------------------------------------

    [Fact]
    public async Task Member_sees_granted_songs_and_nothing_else()
    {
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "in scope"),
            Song(2, TestUsers.OwnerId, "Justice", "Cross", "out of scope"));
        await Grant(options, ShareGrantScope.Album, artistKey: "daft punk", albumKey: "discovery");

        Assert.Equal([1], await VisibleIds(options, TestUsers.FriendId));
    }

    [Fact]
    public async Task Grants_never_cross_grantors_on_an_identical_album_title()
    {
        // L3. Two accounts each own "Daft Punk / Discovery"; a grant from one must not surface the
        // other's copy.
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "granted copy"),
            Song(2, SecondAdminId, "Daft Punk", "Discovery", "look-alike"));
        await Grant(options, ShareGrantScope.Album, artistKey: "daft punk", albumKey: "discovery");

        Assert.Equal([1], await VisibleIds(options, TestUsers.FriendId));
    }

    [Fact]
    public async Task An_admin_can_also_be_a_grantee()
    {
        // Why IsSelf exists and role checks do not: an admin holding a grant has both slice kinds.
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "A", "X", "mine"),
            Song(2, SecondAdminId, "B", "Y", "granted to me"));
        await Grant(options, ShareGrantScope.Library,
            ownerId: SecondAdminId, granteeId: TestUsers.OwnerId, built: true);

        var scope = await Resolve(options, TestUsers.OwnerId);

        Assert.Single(scope.GrantedSlices);
        Assert.Equal([1, 2], await VisibleIds(options, TestUsers.OwnerId));
    }

    [Fact]
    public async Task Grantor_display_name_is_reported_for_attribution()
    {
        var options = NewDb();
        await SeedUser(options, TestUsers.OwnerId, "jeffrey@example.com", "Jeffrey");
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library);

        var scope = await Resolve(options, TestUsers.FriendId);

        Assert.Equal("Jeffrey", Assert.Single(scope.GrantedSlices).GrantorDisplayName);
    }

    [Fact]
    public async Task Grantor_without_a_display_name_never_falls_back_to_their_email()
    {
        // L8. Falling back to Email would publish the grantor's address to every grantee.
        var options = NewDb();
        await SeedUser(options, TestUsers.OwnerId, "private@example.com", displayName: null);
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library);

        var scope = await Resolve(options, TestUsers.FriendId);

        Assert.Null(Assert.Single(scope.GrantedSlices).GrantorDisplayName);
    }

    // --- Revocation -------------------------------------------------------------------------

    [Fact]
    public async Task A_revoked_grant_disappears_on_the_next_request()
    {
        // L9. Grants must be re-read per request. A cached scope — or a grant predicate baked into
        // the compiled EF model — would keep serving a revoked album with no symptom.
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library);

        Assert.Equal([1], await VisibleIds(options, TestUsers.FriendId));

        await using (var db = new MusicHoarderDbContext(options))
        {
            var grant = await db.LibraryShareGrants.IgnoreQueryFilters().SingleAsync();
            grant.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await VisibleIds(options, TestUsers.FriendId));
    }

    // --- Point lookup -------------------------------------------------------------------------

    [Fact]
    public async Task ResolveSong_returns_a_self_slice_for_an_owned_song()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));

        var found = await ResolveSong(options, TestUsers.OwnerId, 1);

        Assert.NotNull(found);
        Assert.True(found!.Value.Slice.IsSelf);
    }

    [Fact]
    public async Task ResolveSong_returns_a_granted_slice_carrying_the_grantor()
    {
        // The like/play handlers branch on IsSelf, so getting this wrong writes a member's like
        // onto the admin's own song row.
        var options = NewDb();
        await SeedUser(options, TestUsers.OwnerId, "jeffrey@example.com", "Jeffrey");
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library);

        var found = await ResolveSong(options, TestUsers.FriendId, 1);

        Assert.NotNull(found);
        Assert.False(found!.Value.Slice.IsSelf);
        Assert.Equal(TestUsers.OwnerId, found.Value.Slice.GrantorUserId);
        Assert.Equal("Jeffrey", found.Value.Slice.GrantorDisplayName);
    }

    [Fact]
    public async Task ResolveSong_refuses_a_song_that_was_never_granted()
    {
        // L2.
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "granted"),
            Song(2, TestUsers.OwnerId, "Justice", "Cross", "not granted"));
        await Grant(options, ShareGrantScope.Album, artistKey: "daft punk", albumKey: "discovery");

        Assert.NotNull(await ResolveSong(options, TestUsers.FriendId, 1));
        Assert.Null(await ResolveSong(options, TestUsers.FriendId, 2));
    }

    [Fact]
    public async Task ResolveSong_refuses_after_the_grant_is_revoked()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library, revoked: true);

        Assert.Null(await ResolveSong(options, TestUsers.FriendId, 1));
    }

    [Fact]
    public async Task ResolveSong_refuses_an_unknown_id_and_an_anonymous_caller()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, "A", "X", "t"));
        await Grant(options, ShareGrantScope.Library);

        Assert.Null(await ResolveSong(options, TestUsers.FriendId, 999));
        Assert.Null(await ResolveSong(options, Guid.Empty, 1));
    }

    // --- helpers -----------------------------------------------------------------------------

    private static DbContextOptions<MusicHoarderDbContext> NewDb() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static ILibraryScopeResolver ResolverFor(Guid callerId)
    {
        var user = callerId == Guid.Empty
            ? null
            : new CurrentUser(callerId, "caller@test.local", UserRole.Member, null);
        return new LibraryScopeResolver(new TestCurrentUserAccessor(user), new SharedLibraryGrantResolver());
    }

    /// <summary>A context scoped to <paramref name="callerId"/>, so the ambient filter is live.</summary>
    private static MusicHoarderDbContext DbFor(
        DbContextOptions<MusicHoarderDbContext> options, Guid callerId)
    {
        var user = callerId == Guid.Empty
            ? null
            : new CurrentUser(callerId, "caller@test.local", UserRole.Member, null);
        return new MusicHoarderDbContext(options, new TestCurrentUserAccessor(user));
    }

    private static async Task<ILibraryScope> Resolve(
        DbContextOptions<MusicHoarderDbContext> options, Guid callerId)
    {
        await using var db = DbFor(options, callerId);
        return await ResolverFor(callerId).ResolveAsync(db, default);
    }

    private static async Task<List<int>> VisibleIds(
        DbContextOptions<MusicHoarderDbContext> options, Guid callerId)
    {
        await using var db = DbFor(options, callerId);
        var scope = await ResolverFor(callerId).ResolveAsync(db, default);

        var ids = new List<int>();
        foreach (var slice in scope.Slices)
            ids.AddRange(await scope.SongsFor(db, slice).Select(s => s.Id).ToListAsync());
        ids.Sort();
        return ids;
    }

    private static async Task<(SongMetadata Song, LibrarySlice Slice)?> ResolveSong(
        DbContextOptions<MusicHoarderDbContext> options, Guid callerId, int songId)
    {
        await using var db = DbFor(options, callerId);
        return await ResolverFor(callerId).ResolveSongAsync(db, songId, default);
    }

    private static async Task Seed(DbContextOptions<MusicHoarderDbContext> options, params SongMetadata[] songs)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.Songs.AddRange(songs);
        await db.SaveChangesAsync();
    }

    private static async Task SeedUser(
        DbContextOptions<MusicHoarderDbContext> options, Guid id, string email, string? displayName)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            EmailNormalized = User.Normalize(email),
            DisplayName = displayName,
            Role = UserRole.Admin,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task Grant(
        DbContextOptions<MusicHoarderDbContext> options,
        ShareGrantScope scope,
        string? artistKey = null,
        string? albumKey = null,
        bool revoked = false,
        Guid? ownerId = null,
        Guid? granteeId = null,
        bool built = true)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.LibraryShareGrants.Add(new LibraryShareGrant
        {
            OwnerUserId = ownerId ?? TestUsers.OwnerId,
            GranteeUserId = granteeId ?? TestUsers.FriendId,
            Scope = scope,
            ArtistKey = artistKey,
            AlbumKey = albumKey,
            CreatedAtUtc = DateTime.UtcNow,
            RevokedAtUtc = revoked ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();

        // A library-scope grant only exposes built rows, so seeded songs need promoting for those
        // tests to mean anything.
        if (scope == ShareGrantScope.Library && built)
        {
            await using var promote = new MusicHoarderDbContext(options);
            var owner = ownerId ?? TestUsers.OwnerId;
            var rows = await promote.Songs.IgnoreQueryFilters()
                .Where(s => s.OwnerUserId == owner).ToListAsync();
            foreach (var row in rows)
            {
                row.LibraryBuildStatus = LibraryBuildStatus.Done;
                row.DestinationPath = $"/dest/{row.Id}.mp3";
            }
            await promote.SaveChangesAsync();
        }
    }

    private static SongMetadata Song(
        int id, Guid ownerId, string artist, string album, string title) => new()
    {
        Id = id,
        OwnerUserId = ownerId,
        SourcePath = $"/music/{ownerId:N}/{artist}/{album}/{title}.mp3",
        FileSizeBytes = 1,
        FileName = $"{title}.mp3",
        Extension = ".mp3",
        Artist = artist,
        Album = album,
        Title = title,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
