using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// The friend-facing read surface: the songs list carries only the deliberate ApiSong subset
/// (no real filesystem paths, no like/play fields), per-song endpoints resolve strictly through
/// grants with uniform 404s, and non-friend callers just see an empty world.
/// </summary>
public class SharedLibraryEndpointsTests
{
    [Fact]
    public async Task ListSharedSongs_returns_granted_songs_with_reduced_shape()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.AddRange(
                Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time", trackNumber: 1),
                Song(2, TestUsers.OwnerId, "Daft Punk", "Discovery", "Aerodynamic", trackNumber: 2),
                Song(3, TestUsers.OwnerId, "Justice", "Cross", "D.A.N.C.E."));
            seed.LibraryShareGrants.Add(AlbumGrant());
            await seed.SaveChangesAsync();
        }

        await using var db = FriendContext(options);
        var payload = Value(await SharedLibraryEndpoints.ListSharedSongs(
            db, FriendAccessor(),
            TestLibraryScope.For((FriendAccessor()).User),
            NullLogger<DeprecatedSharedApi>.Instance, new DefaultHttpContext(), CancellationToken.None));

        Assert.Equal(2, GetProperty<int>(payload, "Count"));
        var songs = ((IEnumerable)GetProperty<object>(payload, "Songs")).Cast<object>().ToList();
        var first = songs[0];

        Assert.Equal("One More Time", GetProperty<string?>(first, "Title"));
        // The owner's real path must not leak; ApiSong just needs the key present.
        Assert.Equal("", GetProperty<string>(first, "SourcePath"));
        Assert.Null(first.GetType().GetProperty("DestinationPath"));
        // Like/play fields exist but are the CALLER's own state (UserSongState), never the
        // owner's columns — with no state rows they read as untouched.
        Assert.Null(GetProperty<DateTime?>(first, "LikedAtUtc"));
        Assert.Equal(0, GetProperty<int>(first, "PlayCount"));
    }

    [Fact]
    public async Task ListSharedSongs_for_owner_or_ungranted_friend_is_empty()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));
            seed.LibraryShareGrants.Add(AlbumGrant());
            await seed.SaveChangesAsync();
        }

        await using (var asOwner = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)))
        {
            var payload = Value(await SharedLibraryEndpoints.ListSharedSongs(
            asOwner, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser),
            TestLibraryScope.For((new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)).User),
            NullLogger<DeprecatedSharedApi>.Instance, new DefaultHttpContext(), CancellationToken.None));
            Assert.Equal(0, GetProperty<int>(payload, "Count"));
        }

        var otherFriend = new CurrentUser(TestUsers.SecondFriendId, "other@test.local", UserRole.Member, null);
        await using var asOther = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(otherFriend));
        var other = Value(await SharedLibraryEndpoints.ListSharedSongs(
            asOther, new TestCurrentUserAccessor(otherFriend),
            TestLibraryScope.For((new TestCurrentUserAccessor(otherFriend)).User),
            NullLogger<DeprecatedSharedApi>.Instance, new DefaultHttpContext(), CancellationToken.None));
        Assert.Equal(0, GetProperty<int>(other, "Count"));
    }

    [Fact]
    public async Task Per_song_endpoints_404_uniformly_out_of_scope()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.AddRange(
                Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "In Scope", syncedLyrics: "[00:01.00] line"),
                Song(2, TestUsers.OwnerId, "Justice", "Cross", "Out Of Scope"));
            seed.LibraryShareGrants.Add(AlbumGrant());
            await seed.SaveChangesAsync();
        }

        await using var db = FriendContext(options);
        var resolver = new SharedLibraryGrantResolver();

        // In scope: lyrics resolve (no disk dependency, unlike stream).
        var lyrics = Value(await SharedLibraryEndpoints.GetSharedLibrarySongLyrics(
            1, db, FriendAccessor(), resolver, CancellationToken.None));
        Assert.Equal("[00:01.00] line", GetProperty<string?>(lyrics, "Synced"));

        // Out of scope / unknown: uniform 404 on every per-song endpoint.
        foreach (var id in new[] { 2, 999 })
        {
            var stream = await SharedLibraryEndpoints.StreamSharedLibrarySong(id, db, FriendAccessor(), resolver, CancellationToken.None);
            Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)stream).StatusCode);

            var lyricsResult = await SharedLibraryEndpoints.GetSharedLibrarySongLyrics(id, db, FriendAccessor(), resolver, CancellationToken.None);
            Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)lyricsResult).StatusCode);
        }
    }

    [Fact]
    public async Task Like_and_played_write_the_friends_own_state_not_the_owners_row()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));
            seed.LibraryShareGrants.Add(AlbumGrant());
            await seed.SaveChangesAsync();
        }

        await using var db = FriendContext(options);
        var resolver = new SharedLibraryGrantResolver();

        var liked = Value(await SharedLibraryEndpoints.LikeSharedSong(1, db, TestLibraryScope.For(FriendAccessor().User), FriendAccessor(), NoopNavidrome.Instance, NoopTrackSync.Instance, CancellationToken.None));
        Assert.NotNull(GetProperty<DateTime?>(liked, "LikedAtUtc"));

        var played = Value(await SharedLibraryEndpoints.ReportSharedSongPlayed(1, db, TestLibraryScope.For(FriendAccessor().User), FriendAccessor(), CancellationToken.None));
        Assert.Equal(1, GetProperty<int>(played, "PlayCount"));

        await using var verify = new MusicHoarderDbContext(options);
        var state = Assert.Single(await verify.UserSongStates.ToListAsync());
        Assert.Equal(TestUsers.FriendId, state.UserId);
        Assert.Equal(1, state.SongId);
        Assert.NotNull(state.LikedAtUtc);
        Assert.Equal(1, state.PlayCount);

        // The owner's own like/play columns are untouched — friend taste never bleeds across.
        var song = await verify.Songs.IgnoreQueryFilters().SingleAsync(s => s.Id == 1);
        Assert.Null(song.LikedAtUtc);
        Assert.Equal(0, song.PlayCount);
    }

    [Fact]
    public async Task Like_out_of_scope_song_404s_and_writes_nothing()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(2, TestUsers.OwnerId, "Justice", "Cross", "Not Shared"));
            seed.LibraryShareGrants.Add(AlbumGrant()); // grants Discovery only
            await seed.SaveChangesAsync();
        }

        await using var db = FriendContext(options);
        var result = await SharedLibraryEndpoints.LikeSharedSong(2, db, TestLibraryScope.For(FriendAccessor().User), FriendAccessor(), NoopNavidrome.Instance, NoopTrackSync.Instance, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
        await using var verify = new MusicHoarderDbContext(options);
        Assert.Empty(await verify.UserSongStates.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Unlike_without_prior_state_is_idempotent()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));
            seed.LibraryShareGrants.Add(AlbumGrant());
            await seed.SaveChangesAsync();
        }

        await using var db = FriendContext(options);
        var result = Value(await SharedLibraryEndpoints.UnlikeSharedSong(1, db, TestLibraryScope.For(FriendAccessor().User), FriendAccessor(), NoopNavidrome.Instance, NoopTrackSync.Instance, CancellationToken.None));

        Assert.Null(GetProperty<DateTime?>(result, "LikedAtUtc"));
    }

    [Fact]
    public async Task ListSharedSongs_carries_the_callers_own_state()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Daft Punk", "Discovery", "One More Time"));
            seed.LibraryShareGrants.AddRange(
                AlbumGrant(),
                new LibraryShareGrant
                {
                    OwnerUserId = TestUsers.OwnerId,
                    GranteeUserId = TestUsers.SecondFriendId,
                    Scope = ShareGrantScope.Album,
                    ArtistKey = "daft punk",
                    AlbumKey = "discovery",
                    CreatedAtUtc = DateTime.UtcNow,
                });
            seed.UserSongStates.Add(new UserSongState
            {
                UserId = TestUsers.FriendId,
                SongId = 1,
                LikedAtUtc = DateTime.UtcNow,
                PlayCount = 7,
            });
            await seed.SaveChangesAsync();
        }

        // First friend sees their like + plays…
        await using (var db = FriendContext(options))
        {
            var payload = Value(await SharedLibraryEndpoints.ListSharedSongs(
            db, FriendAccessor(),
            TestLibraryScope.For((FriendAccessor()).User),
            NullLogger<DeprecatedSharedApi>.Instance, new DefaultHttpContext(), CancellationToken.None));
            var song = ((IEnumerable)GetProperty<object>(payload, "Songs")).Cast<object>().Single();
            Assert.NotNull(GetProperty<DateTime?>(song, "LikedAtUtc"));
            Assert.Equal(7, GetProperty<int>(song, "PlayCount"));
        }

        // …the second friend, granted the same album, sees a clean slate.
        var other = new CurrentUser(TestUsers.SecondFriendId, "other@test.local", UserRole.Member, null);
        await using (var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(other)))
        {
            var payload = Value(await SharedLibraryEndpoints.ListSharedSongs(
            db, new TestCurrentUserAccessor(other),
            TestLibraryScope.For((new TestCurrentUserAccessor(other)).User),
            NullLogger<DeprecatedSharedApi>.Instance, new DefaultHttpContext(), CancellationToken.None));
            var song = ((IEnumerable)GetProperty<object>(payload, "Songs")).Cast<object>().Single();
            Assert.Null(GetProperty<DateTime?>(song, "LikedAtUtc"));
            Assert.Equal(0, GetProperty<int>(song, "PlayCount"));
        }
    }

    // -- helpers --

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static MusicHoarderDbContext FriendContext(DbContextOptions<MusicHoarderDbContext> options) =>
        new(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser));

    private static TestCurrentUserAccessor FriendAccessor() =>
        new(TestCurrentUserAccessor.FriendUser);

    private static LibraryShareGrant AlbumGrant() => new()
    {
        OwnerUserId = TestUsers.OwnerId,
        GranteeUserId = TestUsers.FriendId,
        Scope = ShareGrantScope.Album,
        ArtistKey = "daft punk",
        AlbumKey = "discovery",
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static SongMetadata Song(
        int id,
        Guid ownerId,
        string artist,
        string album,
        string title,
        int? trackNumber = null,
        string? syncedLyrics = null) => new()
    {
        Id = id,
        OwnerUserId = ownerId,
        SourcePath = $"/secret/owner/path/{title}.mp3",
        FileSizeBytes = 1,
        FileName = $"{title}.mp3",
        Extension = ".mp3",
        Artist = artist,
        Album = album,
        Title = title,
        TrackNumber = trackNumber,
        SyncedLyrics = syncedLyrics,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static object Value(IResult result)
        => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return (T)prop!.GetValue(obj)!;
    }
}
