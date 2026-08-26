using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// <c>GET /api/radio</c> where the ranker meets tenancy. What "similar" means is pinned by
/// <c>RadioRankerTests</c>; this covers what the endpoint adds — which rows may seed and fill a
/// station, and that a station cannot become a way to read someone else's library.
/// </summary>
public class GetRadioTests
{
    private static readonly Guid SecondAdminId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Keeps_playing_after_the_only_track_of_an_album()
    {
        var options = NewOptions();
        await Seed(options,
            Song(1, "Kanye West", "Single Track"),
            Song(2, "Kanye West", "Graduation"),
            Song(3, "Kanye West", "Yeezus"));

        var ids = await Radio(options, Admin(TestUsers.OwnerId), seedSongId: 1);

        Assert.Equal([2, 3], ids.Order());
    }

    [Fact]
    public async Task Honours_the_ids_already_in_the_queue()
    {
        var options = NewOptions();
        await Seed(options, Song(1, "A", "X"), Song(2, "A", "Y"), Song(3, "A", "Z"));

        Assert.Equal([3], await Radio(options, Admin(TestUsers.OwnerId), seedSongId: 1, exclude: "1,2"));
    }

    [Fact]
    public async Task Ignores_a_malformed_exclusion_list_rather_than_failing()
    {
        var options = NewOptions();
        await Seed(options, Song(1, "A", "X"), Song(2, "A", "Y"));

        Assert.Equal([2], await Radio(options, Admin(TestUsers.OwnerId), seedSongId: 1, exclude: "abc,,-"));
    }

    [Fact]
    public async Task Never_plays_a_deleted_or_duplicate_row()
    {
        var options = NewOptions();
        var deleted = Song(2, "A", "Y");
        deleted.DeletedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var duplicate = Song(3, "A", "Z");
        duplicate.IsDuplicate = true;
        await Seed(options, Song(1, "A", "X"), deleted, duplicate, Song(4, "A", "W"));

        Assert.Equal([4], await Radio(options, Admin(TestUsers.OwnerId), seedSongId: 1));
    }

    [Fact]
    public async Task Another_owners_songs_never_reach_the_station()
    {
        var options = NewOptions();
        var foreign = Song(2, "A", "Y");
        foreign.OwnerUserId = SecondAdminId;
        await Seed(options, Song(1, "A", "X"), foreign, Song(3, "A", "Z"));

        Assert.Equal([3], await Radio(options, Admin(TestUsers.OwnerId), seedSongId: 1));
    }

    [Fact]
    public async Task A_seed_the_caller_may_not_read_is_not_found()
    {
        // Indistinguishable from a song that does not exist, or song ids become enumerable.
        var options = NewOptions();
        await Seed(options, Song(1, "A", "X"));

        var result = await Invoke(options, Admin(SecondAdminId), seedSongId: 1);

        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public async Task An_unknown_seed_is_not_found()
    {
        var options = NewOptions();
        await Seed(options, Song(1, "A", "X"));

        Assert.Equal(404, StatusOf(await Invoke(options, Admin(TestUsers.OwnerId), seedSongId: 999)));
    }

    [Fact]
    public async Task A_member_gets_a_station_from_the_rows_shared_with_them()
    {
        // Members read the ordinary endpoints; they own no song rows, so the station is entirely
        // granted — the property that makes one unified endpoint safe.
        var options = NewOptions();
        await SeedUser(options, TestUsers.OwnerId, "jeffrey@example.com", "Jeffrey");
        await Seed(options, Song(1, "Daft Punk", "Discovery"), Song(2, "Daft Punk", "Discovery"));
        await GrantAlbum(options, "daft punk", "discovery");

        Assert.Equal([2], await Radio(options, Member(TestUsers.FriendId), seedSongId: 1));
    }

    [Fact]
    public async Task A_member_gets_nothing_outside_the_grant()
    {
        var options = NewOptions();
        await SeedUser(options, TestUsers.OwnerId, "jeffrey@example.com", "Jeffrey");
        await Seed(options,
            Song(1, "Daft Punk", "Discovery"),
            Song(2, "Daft Punk", "Discovery"),
            Song(3, "Daft Punk", "Homework"));
        await GrantAlbum(options, "daft punk", "discovery");

        Assert.Equal([2], await Radio(options, Member(TestUsers.FriendId), seedSongId: 1));
    }

    [Fact]
    public async Task The_limit_is_clamped_rather_than_trusted()
    {
        var options = NewOptions();
        var songs = new List<SongMetadata>();
        for (var id = 1; id <= 80; id++) songs.Add(Song(id, "A", "X"));
        await Seed(options, songs.ToArray());

        Assert.Equal(50, (await Radio(options, Admin(TestUsers.OwnerId), seedSongId: 1, limit: 500)).Count);
        Assert.Single(await Radio(options, Admin(TestUsers.OwnerId), seedSongId: 1, limit: 0));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static CurrentUser Admin(Guid id) => new(id, "admin@test.local", UserRole.Admin, "Admin");
    private static CurrentUser Member(Guid id) => new(id, "member@test.local", UserRole.Member, "Member");

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task<IResult> Invoke(
        DbContextOptions<MusicHoarderDbContext> options,
        CurrentUser caller,
        int seedSongId,
        string? exclude = null,
        int limit = 20)
    {
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(caller));
        return await RadioEndpoints.GetRadio(
            db, TestLibraryScope.For(caller), CancellationToken.None, seedSongId, exclude, limit);
    }

    private static async Task<List<int>> Radio(
        DbContextOptions<MusicHoarderDbContext> options,
        CurrentUser caller,
        int seedSongId,
        string? exclude = null,
        int limit = 20)
    {
        var result = await Invoke(options, caller, seedSongId, exclude, limit);
        var payload = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return ((IReadOnlyList<int>)payload.GetType().GetProperty("SongIds")!.GetValue(payload)!).ToList();
    }

    private static int StatusOf(IResult result) =>
        (int)result.GetType().GetProperty("StatusCode")!.GetValue(result)!;

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
        DbContextOptions<MusicHoarderDbContext> options, string artistKey, string albumKey)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.LibraryShareGrants.Add(new LibraryShareGrant
        {
            OwnerUserId = TestUsers.OwnerId,
            GranteeUserId = TestUsers.FriendId,
            Scope = ShareGrantScope.Album,
            ArtistKey = artistKey,
            AlbumKey = albumKey,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static SongMetadata Song(int id, string artist, string album) => new()
    {
        Id = id,
        OwnerUserId = TestUsers.OwnerId,
        SourcePath = $"/music/{id}.mp3",
        FileName = $"{id}.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = $"track {id}",
        DurationSeconds = 200,
        DestinationPath = $"/dest/{artist}/{album}/{id}.mp3",
        LibraryBuildStatus = LibraryBuildStatus.Done,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
