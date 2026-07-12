using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

public class SharesEndpointsTests
{
    private static readonly Guid OtherOwnerId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public async Task CreateShare_MintsToken_AndReusesActiveShareForSameSongAndScope()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk", title: "One More Time"));
            await seed.SaveChangesAsync();
        }

        await using var db = OwnerContext(options);
        var accessor = new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser);

        var first = Value(await SharesEndpoints.CreateShare(
            new SharesEndpoints.CreateShareRequest(1, "song"), db, accessor, CancellationToken.None));
        var token = GetProperty<string>(first, "Token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal("Song", GetProperty<string>(first, "Scope"));

        // Same song+scope hands back the same link instead of minting a new token.
        var second = Value(await SharesEndpoints.CreateShare(
            new SharesEndpoints.CreateShareRequest(1, "song"), db, accessor, CancellationToken.None));
        Assert.Equal(token, GetProperty<string>(second, "Token"));

        // A different scope is a different link.
        var album = Value(await SharesEndpoints.CreateShare(
            new SharesEndpoints.CreateShareRequest(1, "album"), db, accessor, CancellationToken.None));
        Assert.NotEqual(token, GetProperty<string>(album, "Token"));
    }

    [Fact]
    public async Task CreateShare_AnotherUsersSong_NotFound()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, OtherOwnerId, "Discovery", "Daft Punk"));
            await seed.SaveChangesAsync();
        }

        await using var db = OwnerContext(options);
        var accessor = new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser);

        var result = await SharesEndpoints.CreateShare(
            new SharesEndpoints.CreateShareRequest(1, "song"), db, accessor, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task GetSharePayload_SongScope_ReturnsOnlyTheSharedTrack()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk", title: "One More Time", trackNumber: 1, syncedLyrics: "[00:01.00] one more time"));
            seed.Songs.Add(Song(2, TestUsers.OwnerId, "Discovery", "Daft Punk", title: "Aerodynamic", trackNumber: 2));
            seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok-song"));
            await seed.SaveChangesAsync();
        }

        await using var db = AnonymousContext(options);
        var payload = Value(await SharesEndpoints.GetSharePayload("tok-song", db, CancellationToken.None));

        Assert.Equal("Song", GetProperty<string>(payload, "Scope"));
        Assert.Equal(1, GetProperty<int>(payload, "SharedSongId"));
        var tracks = Tracks(payload);
        var track = Assert.Single(tracks);
        Assert.Equal("One More Time", GetProperty<string>(track, "Title"));
        Assert.True(GetProperty<bool>(track, "HasSyncedLyrics"));
    }

    [Fact]
    public async Task GetSharePayload_AlbumScope_ReturnsAlbumTracks_ScopedToShareOwner()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk", title: "Aerodynamic", trackNumber: 2));
            seed.Songs.Add(Song(2, TestUsers.OwnerId, "Discovery", "Daft Punk", title: "One More Time", trackNumber: 1));
            // Same album tags but a different tenant, a soft-deleted row, and a duplicate — all invisible.
            seed.Songs.Add(Song(3, OtherOwnerId, "Discovery", "Daft Punk", title: "Intruder", trackNumber: 3));
            seed.Songs.Add(Song(4, TestUsers.OwnerId, "Discovery", "Daft Punk", title: "Deleted", deleted: true));
            seed.Songs.Add(Song(5, TestUsers.OwnerId, "Discovery", "Daft Punk", title: "Dupe", duplicate: true));
            // Different album by the same owner — out of scope.
            seed.Songs.Add(Song(6, TestUsers.OwnerId, "Homework", "Daft Punk", title: "Da Funk"));
            seed.SongShares.Add(Share(1, songId: 2, ShareScope.Album, "tok-album"));
            await seed.SaveChangesAsync();
        }

        await using var db = AnonymousContext(options);
        var payload = Value(await SharesEndpoints.GetSharePayload("tok-album", db, CancellationToken.None));

        Assert.Equal("Album", GetProperty<string>(payload, "Scope"));
        Assert.Equal("Discovery", GetProperty<string>(GetProperty<object>(payload, "Album"), "Title"));
        var tracks = Tracks(payload);
        Assert.Equal(2, tracks.Count);
        // Tracklist order, not id order.
        Assert.Equal("One More Time", GetProperty<string>(tracks[0], "Title"));
        Assert.Equal("Aerodynamic", GetProperty<string>(tracks[1], "Title"));
    }

    [Fact]
    public async Task GetSharePayload_UnknownOrRevokedToken_NotFound()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk"));
            seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok-revoked", revoked: true));
            await seed.SaveChangesAsync();
        }

        await using var db = AnonymousContext(options);

        var unknown = await SharesEndpoints.GetSharePayload("no-such-token", db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)unknown).StatusCode);

        var revoked = await SharesEndpoints.GetSharePayload("tok-revoked", db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)revoked).StatusCode);
    }

    [Fact]
    public async Task GetSharedSongLyrics_OutOfScopeSong_NotFound_EvenForSameOwner()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk", syncedLyrics: "[00:01.00] la"));
            seed.Songs.Add(Song(2, TestUsers.OwnerId, "Homework", "Daft Punk", syncedLyrics: "[00:02.00] da"));
            seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok"));
            await seed.SaveChangesAsync();
        }

        await using var db = AnonymousContext(options);

        var ok = Value(await SharesEndpoints.GetSharedSongLyrics("tok", 1, db, CancellationToken.None));
        Assert.Equal("[00:01.00] la", GetProperty<string>(ok, "Synced"));

        // Song 2 belongs to the same owner but is not covered by this share.
        var outOfScope = await SharesEndpoints.GetSharedSongLyrics("tok", 2, db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)outOfScope).StatusCode);
    }

    [Fact]
    public async Task GetSharedSongLyrics_TranscribedOnly_FallsBackToTranscription()
    {
        // The usual reason to transcribe is that LRCLIB had nothing — the share must present
        // the AI lyrics even though PreferredLyricsSource is still the Lrclib default.
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var song = Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk");
            song.TranscribedSyncedLyrics = "[00:01.00] ai line";
            song.TranscribedPlainLyrics = "ai line";
            seed.Songs.Add(song);
            seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok"));
            await seed.SaveChangesAsync();
        }

        await using var db = AnonymousContext(options);

        var payload = Value(await SharesEndpoints.GetSharePayload("tok", db, CancellationToken.None));
        var track = Assert.Single(Tracks(payload));
        Assert.True(GetProperty<bool>(track, "HasSyncedLyrics"));
        Assert.True(GetProperty<bool>(track, "HasPlainLyrics"));

        var lyrics = Value(await SharesEndpoints.GetSharedSongLyrics("tok", 1, db, CancellationToken.None));
        Assert.Equal("[00:01.00] ai line", GetProperty<string?>(lyrics, "Synced"));
        Assert.Equal("ai line", GetProperty<string?>(lyrics, "Plain"));
    }

    [Fact]
    public async Task GetSharedSongLyrics_PreferredTranscribed_WinsOverLrclib()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var song = Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk", syncedLyrics: "[00:01.00] lrclib line");
            song.TranscribedSyncedLyrics = "[00:01.00] ai line";
            song.PreferredLyricsSource = PreferredLyricsSource.Transcribed;
            seed.Songs.Add(song);
            seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok"));
            await seed.SaveChangesAsync();
        }

        await using var db = AnonymousContext(options);
        var lyrics = Value(await SharesEndpoints.GetSharedSongLyrics("tok", 1, db, CancellationToken.None));
        Assert.Equal("[00:01.00] ai line", GetProperty<string?>(lyrics, "Synced"));
    }

    [Fact]
    public async Task GetSharedSongLyrics_BothExist_DefaultPrefersLrclib()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var song = Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk", syncedLyrics: "[00:01.00] lrclib line");
            song.TranscribedSyncedLyrics = "[00:01.00] ai line";
            seed.Songs.Add(song);
            seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok"));
            await seed.SaveChangesAsync();
        }

        await using var db = AnonymousContext(options);
        var lyrics = Value(await SharesEndpoints.GetSharedSongLyrics("tok", 1, db, CancellationToken.None));
        Assert.Equal("[00:01.00] lrclib line", GetProperty<string?>(lyrics, "Synced"));
    }

    [Fact]
    public async Task StreamSharedSong_InScope_StreamsFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mh-share-test-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);
        try
        {
            var options = NewOptions();
            await using (var seed = new MusicHoarderDbContext(options))
            {
                seed.Songs.Add(Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk", sourcePath: tempFile));
                seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok"));
                await seed.SaveChangesAsync();
            }

            await using var db = AnonymousContext(options);
            var result = await SharesEndpoints.StreamSharedSong("tok", 1, db, CancellationToken.None);

            var stream = Assert.IsType<FileStreamHttpResult>(result);
            Assert.Equal("audio/mpeg", stream.ContentType);
            Assert.True(stream.EnableRangeProcessing);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RevokeShare_DisablesTheLink()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song(1, TestUsers.OwnerId, "Discovery", "Daft Punk"));
            seed.SongShares.Add(Share(1, songId: 1, ShareScope.Song, "tok"));
            await seed.SaveChangesAsync();
        }

        await using (var ownerDb = OwnerContext(options))
        {
            var result = await SharesEndpoints.RevokeShare(1, ownerDb, CancellationToken.None);
            Assert.IsType<NoContent>(result);
        }

        await using var db = AnonymousContext(options);
        var payload = await SharesEndpoints.GetSharePayload("tok", db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)payload).StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    /// <summary>Context as the signed-in owner — the EF query filter scopes to their rows.</summary>
    private static MusicHoarderDbContext OwnerContext(DbContextOptions<MusicHoarderDbContext> options) =>
        new(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));

    /// <summary>
    /// Context as an anonymous visitor: the accessor exists but has no user, so the query filter
    /// resolves to OwnerUserId == Guid.Empty (zero rows) — the production condition the public
    /// share endpoints must survive via IgnoreQueryFilters.
    /// </summary>
    private static MusicHoarderDbContext AnonymousContext(DbContextOptions<MusicHoarderDbContext> options) =>
        new(options, new TestCurrentUserAccessor(user: null));

    private static SongMetadata Song(
        int id,
        Guid ownerId,
        string? album,
        string? artist,
        string? title = null,
        int? trackNumber = null,
        string? syncedLyrics = null,
        string? sourcePath = null,
        bool deleted = false,
        bool duplicate = false) => new()
    {
        Id = id,
        OwnerUserId = ownerId,
        SourcePath = sourcePath ?? $"/music/{id}.mp3",
        FileSizeBytes = 1000,
        FileName = $"{id}.mp3",
        Extension = ".mp3",
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Album = album,
        Artist = artist,
        AlbumArtist = artist,
        Title = title ?? $"Track {id}",
        TrackNumber = trackNumber,
        SyncedLyrics = syncedLyrics,
        DeletedAtUtc = deleted ? DateTime.UtcNow : null,
        IsDuplicate = duplicate,
    };

    private static SongShare Share(int id, int songId, ShareScope scope, string token, bool revoked = false) => new()
    {
        Id = id,
        OwnerUserId = TestUsers.OwnerId,
        SongId = songId,
        Scope = scope,
        Token = token,
        CreatedAtUtc = DateTime.UtcNow,
        RevokedAtUtc = revoked ? DateTime.UtcNow : null,
    };

    private static List<object> Tracks(object payload) =>
        ((IEnumerable)GetProperty<object>(payload, "Tracks")).Cast<object>().ToList();

    private static object Value(IResult result)
        => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}");
        return (T)prop.GetValue(obj)!;
    }
}
