using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// <c>GET /songs</c> now serves every account type from one code path, so this is where the
/// isolation guarantee meets the wire. Covers: a member sees only granted rows and only the
/// redacted shape; an admin's payload did not change; two admins never see each other; and a
/// missing song is indistinguishable from an unshared one.
/// </summary>
public class UnifiedSongsEndpointTests
{
    private static readonly Guid SecondAdminId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Admin_with_no_grants_sees_their_own_rows_and_an_empty_grantor_list()
    {
        var options = NewOptions();
        await Seed(options, Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));

        var payload = await List(options, Admin(TestUsers.OwnerId));

        Assert.Equal(1, Get<int>(payload, "Count"));
        Assert.Empty(Rows(payload, "Grantors"));
    }

    [Fact]
    public async Task Admin_rows_still_carry_the_full_owner_shape()
    {
        // The unification must not quietly shrink what an owner sees. These four fields are the
        // canaries: two filesystem, two pipeline.
        var options = NewOptions();
        await Seed(options, Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));

        var row = Rows(await List(options, Admin(TestUsers.OwnerId)), "Songs").Single();

        Assert.NotNull(row.GetType().GetProperty("SourcePath"));
        Assert.NotNull(row.GetType().GetProperty("DestinationPath"));
        Assert.NotNull(row.GetType().GetProperty("EnrichmentStatus"));
        Assert.NotNull(row.GetType().GetProperty("SpotifyLikedAtUtc"));
        Assert.Equal("/music/1.mp3", Get<string>(row, "SourcePath"));
    }

    [Fact]
    public async Task Member_sees_granted_rows_in_the_redacted_shape_with_attribution()
    {
        var options = NewOptions();
        await SeedUser(options, TestUsers.OwnerId, "jeffrey@example.com", "Jeffrey");
        await Seed(options,
            Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"),
            Song(2, TestUsers.OwnerId, "Justice", "Cross", "D.A.N.C.E."));
        await GrantAlbum(options, "daft punk", "discovery");

        var payload = await List(options, Member(TestUsers.FriendId));

        var row = Assert.IsType<SharedSongRowDto>(Rows(payload, "Songs").Single());
        Assert.Equal("One More Time", row.Title);
        Assert.Equal(TestUsers.OwnerId, row.SharedByUserId);
        Assert.Equal("", row.SourcePath);

        var grantor = Assert.IsType<GrantorDto>(Rows(payload, "Grantors").Single());
        Assert.Equal(TestUsers.OwnerId, grantor.UserId);
        Assert.Equal("Jeffrey", grantor.DisplayName);
        Assert.Equal(1, grantor.SongCount);
    }

    [Fact]
    public async Task Member_never_receives_paths_pipeline_state_or_the_owners_spotify_history()
    {
        // L1 at the wire, not just at the type: whatever the projection does, these must be gone.
        var options = NewOptions();
        await Seed(options, Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));
        await GrantAlbum(options, "daft punk", "discovery");

        var row = Rows(await List(options, Member(TestUsers.FriendId)), "Songs").Single();

        foreach (var forbidden in new[]
                 {
                     "DestinationPath", "PreviousDestinationPath", "Fingerprint",
                     "EnrichmentError", "LibraryBuildError", "MatchWarnings",
                     "SpotifyAddedAtUtc", "SpotifyLikedAtUtc", "OwnerUserId",
                 })
        {
            Assert.Null(row.GetType().GetProperty(forbidden));
        }

        Assert.Equal("", Get<string>(row, "SourcePath"));
    }

    [Fact]
    public async Task Member_with_no_grants_sees_nothing_at_all()
    {
        var options = NewOptions();
        await Seed(options, Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));

        var payload = await List(options, Member(TestUsers.FriendId));

        Assert.Equal(0, Get<int>(payload, "Count"));
        Assert.Empty(Rows(payload, "Grantors"));
    }

    [Fact]
    public async Task One_admin_never_sees_another_admins_rows_through_the_endpoint()
    {
        // L4.
        var options = NewOptions();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "A", "X", "mine"),
            Song(2, SecondAdminId, "B", "Y", "theirs"));

        Assert.Equal(1, Get<int>(await List(options, Admin(TestUsers.OwnerId)), "Count"));
        Assert.Equal(1, Get<int>(await List(options, Admin(SecondAdminId)), "Count"));
    }

    [Fact]
    public async Task An_admin_who_holds_a_grant_sees_both_halves_in_their_own_shapes()
    {
        var options = NewOptions();
        await SeedUser(options, SecondAdminId, "other@example.com", "Alex");
        await Seed(options,
            Song(1, TestUsers.OwnerId, "A", "X", "mine"),
            Song(2, SecondAdminId, "B", "Y", "granted to me"));
        await GrantAlbum(options, "b", "y", ownerId: SecondAdminId, granteeId: TestUsers.OwnerId);

        var payload = await List(options, Admin(TestUsers.OwnerId));
        var rows = Rows(payload, "Songs");

        Assert.Equal(2, rows.Count);
        // Own row keeps the full shape; the granted one is redacted. Same array, two shapes.
        Assert.NotNull(rows[0].GetType().GetProperty("DestinationPath"));
        var granted = Assert.IsType<SharedSongRowDto>(rows[1]);
        Assert.Equal(SecondAdminId, granted.SharedByUserId);
        Assert.Equal("Alex", Assert.IsType<GrantorDto>(Rows(payload, "Grantors").Single()).DisplayName);
    }

    [Fact]
    public async Task Missing_and_unshared_songs_are_indistinguishable()
    {
        // L11. Different messages would let a member enumerate a library they were never granted.
        var options = NewOptions();
        await Seed(options,
            Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "granted"),
            Song(2, TestUsers.OwnerId, "Justice", "Cross", "not granted"));
        await GrantAlbum(options, "daft punk", "discovery");

        await using var db = Context(options, Member(TestUsers.FriendId));
        var scope = TestLibraryScope.For(Member(TestUsers.FriendId));

        var unshared = await SongsEndpoints.StreamSong(2, db, scope, CancellationToken.None);
        var missing = await SongsEndpoints.StreamSong(9999, db, scope, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)unshared).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)missing).StatusCode);
        Assert.Equal(Body(missing), Body(unshared));
    }

    [Fact]
    public async Task A_missing_file_never_names_the_grantors_paths_to_a_member()
    {
        // The redaction DTO pins the songs LIST, so it structurally cannot see this path. A shared
        // track whose file is gone (unmounted NAS, or an artist grant covering a never-built row)
        // is routine, and the 404 body used to hand back the grantor's real disk layout.
        var options = NewOptions();
        await Seed(options, Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "gone"));
        await GrantAlbum(options, "daft punk", "discovery");

        await using var db = Context(options, Member(TestUsers.FriendId));
        var result = await SongsEndpoints.StreamSong(
            1, db, TestLibraryScope.For(Member(TestUsers.FriendId)), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
        var body = Body(result);
        Assert.DoesNotContain("/music/", body);
        Assert.DoesNotContain("sourcePath", body);
        Assert.DoesNotContain("destinationPath", body);
    }

    [Fact]
    public async Task An_admin_still_sees_the_paths_for_their_own_missing_file()
    {
        // The other half: those paths are the whole point of the message for whoever can act on it.
        var options = NewOptions();
        await Seed(options, Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "gone"));

        await using var db = Context(options, Admin(TestUsers.OwnerId));
        var result = await SongsEndpoints.StreamSong(
            1, db, TestLibraryScope.For(Admin(TestUsers.OwnerId)), CancellationToken.None);

        Assert.Contains("/music/1.mp3", Body(result));
    }

    [Fact]
    public async Task Deleted_and_status_filters_never_widen_what_a_member_sees()
    {
        // includeDeleted is a pipeline-triage flag over rows you own. It must not become a way to
        // ask for a grantor's soft-deleted rows.
        var options = NewOptions();
        var deleted = Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "deleted");
        deleted.DeletedAtUtc = DateTime.UtcNow;
        await Seed(options, deleted, Song(2, TestUsers.OwnerId, "Daft Punk", "Discovery", "live"));
        await GrantAlbum(options, "daft punk", "discovery");

        var payload = await List(options, Member(TestUsers.FriendId), includeDeleted: true);

        var row = Assert.IsType<SharedSongRowDto>(Rows(payload, "Songs").Single());
        Assert.Equal("live", row.Title);
    }

    // --- helpers -----------------------------------------------------------------------------

    private static CurrentUser Admin(Guid id) => new(id, "admin@test.local", UserRole.Admin, "Admin");
    private static CurrentUser Member(Guid id) => new(id, "member@test.local", UserRole.Member, "Member");

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static MusicHoarderDbContext Context(
        DbContextOptions<MusicHoarderDbContext> options, CurrentUser caller) =>
        new(options, new TestCurrentUserAccessor(caller));

    private static async Task<object> List(
        DbContextOptions<MusicHoarderDbContext> options, CurrentUser caller, bool includeDeleted = false)
    {
        await using var db = Context(options, caller);
        var result = await ListSongsCaller.Invoke(db, includeDeleted: includeDeleted, caller: caller);
        return result.GetType().GetProperty("Value")!.GetValue(result)!;
    }

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    private static List<object> Rows(object payload, string name) =>
        ((IEnumerable)Get<object>(payload, name)).Cast<object>().ToList();

    private static string Body(IResult result)
    {
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    private static async Task Seed(
        DbContextOptions<MusicHoarderDbContext> options, params SongMetadata[] songs)
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

    private static async Task GrantAlbum(
        DbContextOptions<MusicHoarderDbContext> options,
        string artistKey,
        string albumKey,
        Guid? ownerId = null,
        Guid? granteeId = null)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.LibraryShareGrants.Add(new LibraryShareGrant
        {
            OwnerUserId = ownerId ?? TestUsers.OwnerId,
            GranteeUserId = granteeId ?? TestUsers.FriendId,
            Scope = ShareGrantScope.Album,
            ArtistKey = artistKey,
            AlbumKey = albumKey,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static SongMetadata Song(
        int id, Guid ownerId, string artist, string album, string title) => new()
    {
        Id = id,
        OwnerUserId = ownerId,
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
