using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Tests.Endpoints;

public class LikesAndPlaysTests
{
    /// <summary>No-op enqueuers — these tests exercise the DB side, not the Navidrome / instance push.</summary>
    private static readonly INavidromeLikeEnqueuer Noop = new NoopEnqueuer();
    private static readonly ITrackSyncEnqueuer NoopSync = new NoopSyncEnqueuer();

    private sealed class NoopEnqueuer : INavidromeLikeEnqueuer
    {
        public void TryEnqueue(int songId, Guid ownerUserId) { }
    }

    private sealed class NoopSyncEnqueuer : ITrackSyncEnqueuer
    {
        public void TryEnqueue(int songId, Guid ownerUserId) { }
    }

    [Fact]
    public async Task LikeSong_SetsLikedAtUtc_AndIsIdempotent()
    {
        var options = NewOptions();
        int songId;
        await using (var db = NewContext(options))
        {
            var song = NewSong("/a.mp3", "a.mp3");
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            songId = song.Id;
        }

        await using (var db = NewContext(options))
        {
            var result = await SongsEndpoints.LikeSong(songId, db, Noop, NoopSync, CancellationToken.None);
            Assert.NotNull(GetProperty<DateTime?>(ResultValue(result), "LikedAtUtc"));
        }

        DateTime? first;
        await using (var db = NewContext(options))
        {
            first = (await db.Songs.SingleAsync(s => s.Id == songId)).LikedAtUtc;
            Assert.NotNull(first);
        }

        // Re-liking keeps the original timestamp (idempotent).
        await using (var db = NewContext(options))
        {
            await SongsEndpoints.LikeSong(songId, db, Noop, NoopSync, CancellationToken.None);
        }

        await using (var db = NewContext(options))
        {
            var again = (await db.Songs.SingleAsync(s => s.Id == songId)).LikedAtUtc;
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public async Task UnlikeSong_ClearsLikedAtUtc()
    {
        var options = NewOptions();
        int songId;
        await using (var db = NewContext(options))
        {
            var song = NewSong("/a.mp3", "a.mp3");
            song.LikedAtUtc = DateTime.UtcNow;
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            songId = song.Id;
        }

        await using (var db = NewContext(options))
        {
            await SongsEndpoints.UnlikeSong(songId, db, Noop, NoopSync, CancellationToken.None);
        }

        await using (var db = NewContext(options))
        {
            Assert.Null((await db.Songs.SingleAsync(s => s.Id == songId)).LikedAtUtc);
        }
    }

    [Fact]
    public async Task ReportPlayed_BumpsCountAndTimestamp()
    {
        var options = NewOptions();
        int songId;
        await using (var db = NewContext(options))
        {
            var song = NewSong("/a.mp3", "a.mp3");
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            songId = song.Id;
        }

        await using (var db = NewContext(options))
        {
            await SongsEndpoints.ReportPlayed(songId, db, CancellationToken.None);
            await SongsEndpoints.ReportPlayed(songId, db, CancellationToken.None);
        }

        await using (var db = NewContext(options))
        {
            var song = await db.Songs.SingleAsync(s => s.Id == songId);
            Assert.Equal(2, song.PlayCount);
            Assert.NotNull(song.LastPlayedAtUtc);
        }
    }

    [Fact]
    public async Task LikeSong_ForeignTenantSong_IsNotFound()
    {
        var options = NewOptions();
        int songId;
        // Seed with the unfiltered context: the song belongs to the DEMO tenant.
        await using (var db = NewContext(options))
        {
            var song = NewSong("/demo.mp3", "demo.mp3");
            song.OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.DemoId;
            db.Songs.Add(song);
            await db.SaveChangesAsync();
            songId = song.Id;
        }

        // The OWNER-scoped context must not see (or like) it.
        await using (var db = new MusicHoarderDbContext(
            options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)))
        {
            var result = await SongsEndpoints.LikeSong(songId, db, Noop, NoopSync, CancellationToken.None);
            Assert.Equal(StatusCodes.Status404NotFound, GetStatusCode(result));
        }

        await using (var db = NewContext(options))
        {
            Assert.Null((await db.Songs.SingleAsync(s => s.Id == songId)).LikedAtUtc);
        }
    }

    [Fact]
    public async Task ListSongs_ExposesLikeAndPlayFields()
    {
        var options = NewOptions();
        await using (var db = NewContext(options))
        {
            var song = NewSong("/a.mp3", "a.mp3");
            song.LikedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            song.PlayCount = 5;
            song.LastPlayedAtUtc = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
            db.Songs.Add(song);
            await db.SaveChangesAsync();
        }

        await using (var db2 = NewContext(options))
        {
            var result = await SongsEndpoints.ListSongs(db2);
            var value = ResultValue(result);
            var songs = ((System.Collections.IEnumerable)value.GetType().GetProperty("Songs")!.GetValue(value)!)
                .Cast<object>().ToList();
            var first = Assert.Single(songs);

            Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), GetProperty<DateTime?>(first, "LikedAtUtc"));
            Assert.Equal(5, GetProperty<int>(first, "PlayCount"));
            Assert.Equal(new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc), GetProperty<DateTime?>(first, "LastPlayedAtUtc"));
        }
    }

    private static object ResultValue(IResult result) =>
        result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static int GetStatusCode(IResult result) =>
        (int)result.GetType().GetProperty("StatusCode")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}");
        return (T?)prop.GetValue(obj)!;
    }

    private static SongMetadata NewSong(string sourcePath, string fileName) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static MusicHoarderDbContext NewContext(DbContextOptions<MusicHoarderDbContext> options) =>
        new(options);
}
