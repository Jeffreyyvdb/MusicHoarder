using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Listening;

public class ListeningEventWriteTests
{
    [Fact]
    public async Task An_owner_play_appends_one_event_with_the_songs_tags()
    {
        var options = NewOptions();
        await Seed(options);

        await using (var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(Admin)))
        {
            await SongsEndpoints.ReportPlayed(
                1, db, TestLibraryScope.For(Admin), new TestCurrentUserAccessor(Admin), CancellationToken.None);
        }

        await using var verify = new MusicHoarderDbContext(options);
        var played = Assert.Single(await verify.ListeningEvents.ToListAsync());
        Assert.Equal(TestUsers.OwnerId, played.UserId);
        Assert.Equal(1, played.SongId);
        Assert.Equal("One More Time", played.Title);
        Assert.Equal("Daft Punk", played.Artist);
        Assert.Equal("Discovery", played.Album);
        Assert.Equal("House; Electronic", played.Genre);
        Assert.Equal(320, played.DurationSeconds);

        var song = await verify.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == 1);
        Assert.Equal(1, song.PlayCount);
        Assert.Equal(song.LastPlayedAtUtc, played.PlayedAtUtc);
    }

    [Fact]
    public async Task A_grantee_play_appends_one_event_for_the_grantee_not_the_owner()
    {
        var options = NewOptions();
        await Seed(options);

        await using (var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(Member)))
        {
            await SongsEndpoints.ReportPlayed(
                1, db, TestLibraryScope.For(Member), new TestCurrentUserAccessor(Member), CancellationToken.None);
        }

        await using var verify = new MusicHoarderDbContext(options);
        var played = Assert.Single(await verify.ListeningEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(TestUsers.FriendId, played.UserId);
        Assert.Equal("One More Time", played.Title);
        Assert.Equal(1, Assert.Single(await verify.UserSongStates.ToListAsync()).PlayCount);
    }

    [Fact]
    public async Task A_play_of_a_song_the_caller_cannot_see_records_nothing()
    {
        var options = NewOptions();
        await Seed(options, ungranted: true);

        await using (var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(Member)))
        {
            await SongsEndpoints.ReportPlayed(
                2, db, TestLibraryScope.For(Member), new TestCurrentUserAccessor(Member), CancellationToken.None);
        }

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Empty(await verify.ListeningEvents.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Each_play_of_the_same_song_is_its_own_event()
    {
        var options = NewOptions();
        await Seed(options);

        await using (var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(Member)))
        {
            var scope = TestLibraryScope.For(Member);
            var accessor = new TestCurrentUserAccessor(Member);
            await SongsEndpoints.ReportPlayed(1, db, scope, accessor, CancellationToken.None);
            await SongsEndpoints.ReportPlayed(1, db, scope, accessor, CancellationToken.None);
        }

        await using var verify = new MusicHoarderDbContext(options);
        Assert.Equal(2, await verify.ListeningEvents.IgnoreQueryFilters().CountAsync());
    }

    private static CurrentUser Admin =>
        new(TestUsers.OwnerId, "admin@test.local", UserRole.Admin, "Admin");

    private static CurrentUser Member =>
        new(TestUsers.FriendId, "member@test.local", UserRole.Member, "Member", Capability.TrackListening);

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task Seed(DbContextOptions<MusicHoarderDbContext> options, bool ungranted = false)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.Songs.Add(Song(1, "Daft Punk", "Discovery", "One More Time"));
        if (ungranted)
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
        Genre = "House; Electronic",
        DurationSeconds = 320,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
