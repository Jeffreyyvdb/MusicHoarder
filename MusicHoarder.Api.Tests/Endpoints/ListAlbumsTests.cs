using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// <c>GET /api/albums</c> where the projection meets tenancy. The grouping rules themselves are
/// pinned by <c>AlbumProjectionTests</c>; this covers what the endpoint adds — which rows reach the
/// projection, and what a grantee's cards are allowed to be built from.
/// </summary>
public class ListAlbumsTests
{
    private static readonly Guid SecondAdminId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Only_built_songs_make_up_the_grid()
    {
        var options = NewOptions();
        await Seed(options,
            Built(1, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac"),
            Unbuilt(2, "Justice", "Cross"));

        var albums = await List(options, Admin(TestUsers.OwnerId));

        Assert.Equal("Discovery", Assert.Single(albums).Title);
    }

    [Fact]
    public async Task Unbuilt_songs_group_too_when_the_caller_asks_for_them()
    {
        // The song-detail panel resolves a song's album context against this set, so a row still in
        // the inbox has to land somewhere.
        var options = NewOptions();
        await Seed(options,
            Built(1, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac"),
            Unbuilt(2, "Justice", "Cross"));

        var albums = await List(options, Admin(TestUsers.OwnerId), builtOnly: false);

        // Ordered by artist, so Daft Punk precedes Justice.
        Assert.Equal(["Discovery", "Cross"], albums.Select(a => a.Title));
        // The unbuilt one has no folder, so it groups on its name key.
        Assert.Equal("justice::cross", albums[1].Key);
    }

    [Fact]
    public async Task Merging_is_what_folds_two_folders_of_one_album_together()
    {
        var options = NewOptions();
        await Seed(options,
            Built(1, "Kanye West", "Ye", "/dest/Kanye West/2018 - Ye/01.flac"),
            Built(2, "Kanye West", "Ye", "/dest/Kanye West/2021 - Ye/01.flac"));

        Assert.Equal(2, (await List(options, Admin(TestUsers.OwnerId), merge: false)).Count);

        var merged = Assert.Single(await List(options, Admin(TestUsers.OwnerId)));
        Assert.Equal(2, merged.TrackCount);
        Assert.Equal(2, merged.FolderKeys.Count);
    }

    [Fact]
    public async Task Another_owners_albums_are_invisible()
    {
        var options = NewOptions();
        await Seed(options, Built(1, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac"));

        Assert.Empty(await List(options, Admin(SecondAdminId)));
    }

    // ── what a grantee's cards may be built from ──────────────────────────────

    [Fact]
    public async Task A_grantees_albums_group_by_name_because_they_are_told_no_folder()
    {
        // The server can see the grantor's destination path; publishing it — even folded into a card
        // key — would leak their disk layout, which is exactly what SharedSongRowDto withholds.
        var options = NewOptions();
        await SeedUser(options, TestUsers.OwnerId, "jeffrey@example.com", "Jeffrey");
        await Seed(options, Built(1, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac"));
        await GrantAlbum(options, "daft punk", "discovery");

        var album = Assert.Single(await List(options, Member(TestUsers.FriendId)));

        Assert.Equal("daft punk::discovery", album.Key);
        Assert.Equal(["daft punk::discovery"], album.FolderKeys);
        Assert.DoesNotContain(album.FolderKeys, k => k.Contains('/'));
    }

    [Fact]
    public async Task A_grantors_spotify_save_history_never_dates_a_grantees_album()
    {
        // "Recently added" is an ordering, and an ordering built from someone's private save dates
        // publishes them just as surely as a field would.
        var options = NewOptions();
        await SeedUser(options, TestUsers.OwnerId, "jeffrey@example.com", "Jeffrey");
        var song = Built(1, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac");
        song.AcquiredAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await Seed(options, song);
        await SeedWishlistLink(options, songId: 1, spotifyAddedAtUtc: new DateTime(2015, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        await GrantAlbum(options, "daft punk", "discovery");

        var granteeAlbum = Assert.Single(await List(options, Member(TestUsers.FriendId)));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), granteeAlbum.AddedAtUtc);

        // ...while the owner's own card does use it, which is the whole point of the field.
        var ownerAlbum = Assert.Single(await List(options, Admin(TestUsers.OwnerId)));
        Assert.Equal(new DateTime(2015, 5, 5, 0, 0, 0, DateTimeKind.Utc), ownerAlbum.AddedAtUtc);
    }

    // ── the browse filters ────────────────────────────────────────────────────

    [Fact]
    public async Task The_artist_filter_matches_the_credit_and_every_discrete_artist()
    {
        var options = NewOptions();
        var collab = Built(2, "21 Savage", "Savage Mode II", "/dest/21 Savage/2020 - Savage Mode II/01.flac");
        collab.Artists = "21 Savage; Metro Boomin";
        await Seed(options, Built(1, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac"), collab);

        Assert.Equal("Discovery",
            Assert.Single(await List(options, Admin(TestUsers.OwnerId), artist: "Daft Punk")).Title);
        // Reachable from a name that only appears in the discrete credits.
        Assert.Equal("Savage Mode II",
            Assert.Single(await List(options, Admin(TestUsers.OwnerId), artist: "Metro Boomin")).Title);
    }

    [Fact]
    public async Task The_year_filter_covers_the_no_year_bucket()
    {
        var options = NewOptions();
        var dated = Built(1, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac");
        dated.Year = 2001;
        await Seed(options, dated, Built(2, "Justice", "Cross", "/dest/Justice/Cross/01.flac"));

        Assert.Equal("Discovery",
            Assert.Single(await List(options, Admin(TestUsers.OwnerId), year: "2001")).Title);
        Assert.Equal("Cross",
            Assert.Single(await List(options, Admin(TestUsers.OwnerId), year: "unknown")).Title);
    }

    [Fact]
    public async Task The_unreleased_filter_narrows_to_leaked_material()
    {
        var options = NewOptions();
        var leak = Built(1, "Kanye West", "Yandhi", "/dest/Kanye West/Yandhi/01.flac");
        leak.MatchedBy = "YeTracker";
        leak.MatchWarnings = """["category:unreleased"]""";
        var released = Built(2, "Daft Punk", "Discovery", "/dest/Daft Punk/2001 - Discovery/01.flac");
        released.MatchedBy = "SpotifyAPI";
        released.EnrichmentStatus = EnrichmentStatus.Matched;
        released.Isrc = "GBDUW0000059";
        await Seed(options, leak, released);

        var albums = await List(options, Admin(TestUsers.OwnerId), unreleased: true);

        Assert.Equal("Yandhi", Assert.Single(albums).Title);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static CurrentUser Admin(Guid id) => new(id, "admin@test.local", UserRole.Admin, "Admin");
    private static CurrentUser Member(Guid id) => new(id, "member@test.local", UserRole.Member, "Member");

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task<List<AlbumSummaryDto>> List(
        DbContextOptions<MusicHoarderDbContext> options,
        CurrentUser caller,
        bool builtOnly = true,
        bool merge = true,
        string? artist = null,
        string? year = null,
        bool unreleased = false)
    {
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(caller));
        var result = await AlbumsEndpoints.ListAlbums(
            db, TestLibraryScope.For(caller), CancellationToken.None,
            builtOnly, merge, artist, year, unreleased);
        var payload = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return (List<AlbumSummaryDto>)payload.GetType().GetProperty("Albums")!.GetValue(payload)!;
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

    private static async Task SeedWishlistLink(
        DbContextOptions<MusicHoarderDbContext> options, int songId, DateTime spotifyAddedAtUtc)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = TestUsers.OwnerId,
            Artist = "Daft Punk",
            Title = "One More Time",
            DownloadedSongId = songId,
            SpotifyAddedAtUtc = spotifyAddedAtUtc,
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

    private static SongMetadata Built(int id, string artist, string album, string destinationPath)
    {
        var song = Unbuilt(id, artist, album);
        song.DestinationPath = destinationPath;
        song.LibraryBuildStatus = LibraryBuildStatus.Done;
        return song;
    }

    private static SongMetadata Unbuilt(int id, string artist, string album) => new()
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
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
