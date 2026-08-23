using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Sharing;

/// <summary>
/// Grant membership must match exactly what was granted and nothing else: album grants use the
/// same lowercased (album-artist ?? artist, album) keys as anonymous shares, library grants only
/// expose the built library, revocation hides immediately, and one friend's grants never leak to
/// another. Scoping is by the grant's own OwnerUserId, so a look-alike song in another tenant
/// (e.g. demo) is never in scope.
/// </summary>
public class SharedLibraryGrantResolverTests
{
    [Fact]
    public async Task Album_grant_exposes_exactly_that_album()
    {
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, artist: "Daft Punk", album: "Discovery", title: "One More Time"),
            Song(2, TestUsers.OwnerId, artist: "Daft Punk", album: "Discovery", title: "Aerodynamic"),
            Song(3, TestUsers.OwnerId, artist: "Daft Punk", album: "Homework", title: "Around the World"),
            Song(4, TestUsers.OwnerId, artist: "Justice", album: "Cross", title: "D.A.N.C.E."));
        await Grant(options, Scope: ShareGrantScope.Album, artistKey: "daft punk", albumKey: "discovery");

        var ids = await SharedSongIds(options);

        Assert.Equal([1, 2], ids);
    }

    [Fact]
    public async Task Album_grant_matches_case_insensitively_and_via_album_artist_fallback()
    {
        var options = NewDb();
        await Seed(options,
            // AlbumArtist set → it wins over Artist for the key, same as LoadSongsInScopeAsync.
            Song(1, TestUsers.OwnerId, artist: "Daft Punk feat. Someone", album: "DISCOVERY", title: "t1", albumArtist: "DAFT PUNK"),
            Song(2, TestUsers.OwnerId, artist: "Daft Punk", album: "Homework", title: "t2"));
        await Grant(options, Scope: ShareGrantScope.Album, artistKey: "daft punk", albumKey: "discovery");

        var ids = await SharedSongIds(options);

        Assert.Equal([1], ids);
    }

    [Fact]
    public async Task Artist_grant_exposes_all_albums_of_that_artist_only()
    {
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, artist: "Daft Punk", album: "Discovery", title: "t1"),
            Song(2, TestUsers.OwnerId, artist: "Daft Punk", album: "Homework", title: "t2"),
            Song(3, TestUsers.OwnerId, artist: "Justice", album: "Cross", title: "t3"));
        await Grant(options, Scope: ShareGrantScope.Artist, artistKey: "daft punk");

        var ids = await SharedSongIds(options);

        Assert.Equal([1, 2], ids);
    }

    [Fact]
    public async Task Library_grant_exposes_built_songs_only()
    {
        var options = NewDb();
        var built = Song(1, TestUsers.OwnerId, artist: "A", album: "X", title: "built");
        built.LibraryBuildStatus = LibraryBuildStatus.Done;
        var deleted = Song(3, TestUsers.OwnerId, artist: "C", album: "Z", title: "deleted");
        deleted.LibraryBuildStatus = LibraryBuildStatus.Done;
        deleted.SoftDelete();
        var duplicate = Song(4, TestUsers.OwnerId, artist: "D", album: "W", title: "dupe");
        duplicate.LibraryBuildStatus = LibraryBuildStatus.Done;
        duplicate.IsDuplicate = true;
        await Seed(options,
            built,
            Song(2, TestUsers.OwnerId, artist: "B", album: "Y", title: "pending"), // not built
            deleted,
            duplicate);
        await Grant(options, Scope: ShareGrantScope.Library);

        var ids = await SharedSongIds(options);

        Assert.Equal([1], ids);
    }

    [Fact]
    public async Task Revoked_grant_hides_songs()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, artist: "A", album: "X", title: "t"));
        await Grant(options, Scope: ShareGrantScope.Album, artistKey: "a", albumKey: "x", revoked: true);

        Assert.Empty(await SharedSongIds(options));
        await using var db = new MusicHoarderDbContext(options);
        Assert.Null(await new SharedLibraryGrantResolver().ResolveSongAsync(db, TestUsers.FriendId, 1, default));
    }

    [Fact]
    public async Task Second_friend_sees_nothing_from_first_friends_grants()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, artist: "A", album: "X", title: "t"));
        await Grant(options, Scope: ShareGrantScope.Library);

        var resolver = new SharedLibraryGrantResolver();
        await using var db = new MusicHoarderDbContext(options);
        Assert.Empty(await resolver.ResolveAsync(db, TestUsers.SecondFriendId, default));
        Assert.Null(await resolver.ResolveSongAsync(db, TestUsers.SecondFriendId, 1, default));
    }

    [Fact]
    public async Task Demo_songs_with_matching_keys_are_never_in_scope()
    {
        var options = NewDb();
        var demoSong = Song(2, TestUsers.DemoId, artist: "Daft Punk", album: "Discovery", title: "demo copy");
        await Seed(options,
            Song(1, TestUsers.OwnerId, artist: "Daft Punk", album: "Discovery", title: "owner copy"),
            demoSong);
        await Grant(options, Scope: ShareGrantScope.Album, artistKey: "daft punk", albumKey: "discovery");

        var ids = await SharedSongIds(options);

        Assert.Equal([1], ids); // scoping is by the grant's OwnerUserId, not by name keys alone
    }

    [Fact]
    public async Task ResolveSong_returns_song_only_when_granted()
    {
        var options = NewDb();
        await Seed(options,
            Song(1, TestUsers.OwnerId, artist: "Daft Punk", album: "Discovery", title: "in scope"),
            Song(2, TestUsers.OwnerId, artist: "Justice", album: "Cross", title: "out of scope"));
        await Grant(options, Scope: ShareGrantScope.Album, artistKey: "daft punk", albumKey: "discovery");

        var resolver = new SharedLibraryGrantResolver();
        await using var db = new MusicHoarderDbContext(options);
        Assert.NotNull(await resolver.ResolveSongAsync(db, TestUsers.FriendId, 1, default));
        Assert.Null(await resolver.ResolveSongAsync(db, TestUsers.FriendId, 2, default));
        Assert.Null(await resolver.ResolveSongAsync(db, TestUsers.FriendId, 999, default));
    }

    [Fact]
    public async Task Owner_is_not_their_own_grantee()
    {
        var options = NewDb();
        await Seed(options, Song(1, TestUsers.OwnerId, artist: "A", album: "X", title: "t"));
        await Grant(options, Scope: ShareGrantScope.Library);

        var resolver = new SharedLibraryGrantResolver();
        await using var db = new MusicHoarderDbContext(options);
        // The owner resolving "shared with me" must not count grants they handed out.
        Assert.Empty(await resolver.ResolveAsync(db, TestUsers.OwnerId, default));
    }

    // -- helpers --

    private static DbContextOptions<MusicHoarderDbContext> NewDb() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task Seed(DbContextOptions<MusicHoarderDbContext> options, params SongMetadata[] songs)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.Songs.AddRange(songs);
        await db.SaveChangesAsync();
    }

    private static async Task Grant(
        DbContextOptions<MusicHoarderDbContext> options,
        ShareGrantScope Scope,
        string? artistKey = null,
        string? albumKey = null,
        bool revoked = false,
        Guid? granteeId = null)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.LibraryShareGrants.Add(new LibraryShareGrant
        {
            OwnerUserId = TestUsers.OwnerId,
            GranteeUserId = granteeId ?? TestUsers.FriendId,
            Scope = Scope,
            ArtistKey = artistKey,
            AlbumKey = albumKey,
            CreatedAtUtc = DateTime.UtcNow,
            RevokedAtUtc = revoked ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<int>> SharedSongIds(DbContextOptions<MusicHoarderDbContext> options)
    {
        var resolver = new SharedLibraryGrantResolver();
        await using var db = new MusicHoarderDbContext(options);
        var sets = await resolver.ResolveAsync(db, TestUsers.FriendId, default);
        var ids = new List<int>();
        foreach (var set in sets)
            ids.AddRange(await resolver.ScopeSongs(db, set).Select(s => s.Id).ToListAsync());
        ids.Sort();
        return ids;
    }

    private static SongMetadata Song(
        int id,
        Guid ownerId,
        string artist,
        string album,
        string title,
        string? albumArtist = null) => new()
    {
        Id = id,
        OwnerUserId = ownerId,
        SourcePath = $"/music/{ownerId:N}/{artist}/{album}/{title}.mp3",
        FileSizeBytes = 1,
        FileName = $"{title}.mp3",
        Extension = ".mp3",
        Artist = artist,
        AlbumArtist = albumArtist,
        Album = album,
        Title = title,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
