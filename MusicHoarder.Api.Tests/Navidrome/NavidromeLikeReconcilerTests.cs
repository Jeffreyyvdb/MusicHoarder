using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Navidrome;

public class NavidromeLikeReconcilerTests
{
    private const string SourceDir = "/music/source";
    private const string DestDir = "/music/destination";
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    // ── Push (MH → Navidrome) ────────────────────────────────────────────────

    [Fact]
    public async Task Push_LikedSong_NotYetStarred_StarsAndRecordsBase()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: true, mergeBase: null));
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "A/Al/01.flac")] }; // exists in Navidrome, not starred
        await Reconciler(db, client).ReconcileAllAsync(default);

        Assert.Contains("nav1", client.Starred);
        var song = await db.Songs.SingleAsync();
        Assert.True(song.LikeLastSyncedValue);
        Assert.Equal("nav1", song.NavidromeSongId);
    }

    [Fact]
    public async Task Push_UnlikedSong_StillStarred_Unstars()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: false, mergeBase: true)); // user just unliked in MH
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "A/Al/01.flac")], Starred = { "nav1" } };
        await Reconciler(db, client).ReconcileAllAsync(default);

        Assert.DoesNotContain("nav1", client.Starred);
        Assert.False((await db.Songs.SingleAsync()).LikeLastSyncedValue);
    }

    // ── Pull (Navidrome → MH) ────────────────────────────────────────────────

    [Fact]
    public async Task Pull_StarredInNavidrome_NotLikedInMh_LikesLocally()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: false, mergeBase: null));
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "A/Al/01.flac")], Starred = { "nav1" } };
        await Reconciler(db, client).ReconcileAllAsync(default);

        var song = await db.Songs.SingleAsync();
        Assert.True(song.IsLiked);
        Assert.True(song.LikeLastSyncedValue);
        Assert.Empty(client.Ops); // pull is DB-only, no remote writes
    }

    [Fact]
    public async Task Pull_UnstarredInNavidrome_WasSyncedLiked_UnlikesLocally()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: true, mergeBase: true)); // agreed liked previously
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "A/Al/01.flac")] }; // no longer starred remotely
        await Reconciler(db, client).ReconcileAllAsync(default);

        Assert.False((await db.Songs.SingleAsync()).IsLiked);
        Assert.Empty(client.Ops);
    }

    [Fact]
    public async Task Pull_MatchesBySourcePath_AcrossLibraries()
    {
        await using var db = CreateDb();
        // Enrichment reorganized the destination, but the source path is unchanged and equals the star's path.
        var song = Song(1, sourceRel: "Raw/loose file.mp3", destRel: "Clean/Album/01.flac", liked: false, mergeBase: null);
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "Raw/loose file.mp3")], Starred = { "nav1" } };
        await Reconciler(db, client).ReconcileAllAsync(default);

        Assert.True((await db.Songs.SingleAsync()).IsLiked);
    }

    // ── Agreement / no-op ────────────────────────────────────────────────────

    [Fact]
    public async Task Agreement_RecordsBase_WithoutRemoteWrites()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: true, mergeBase: null));
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "A/Al/01.flac")], Starred = { "nav1" } };
        await Reconciler(db, client).ReconcileAllAsync(default);

        Assert.Empty(client.Ops); // already in agreement
        Assert.True((await db.Songs.SingleAsync()).LikeLastSyncedValue); // base advanced from null → true
    }

    [Fact]
    public async Task LikedSong_WithNoNavidromeMatch_IsLeftPendingForRetry()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: true, mergeBase: null));
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [] }; // track not in Navidrome yet
        await Reconciler(db, client).ReconcileAllAsync(default);

        Assert.Empty(client.Ops);
        Assert.Null((await db.Songs.SingleAsync()).LikeLastSyncedValue); // base stays null → retried next sweep
    }

    [Fact]
    public async Task Push_StarsEveryLibraryCopyOfTheSameSong()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: true, mergeBase: null));
        await db.SaveChangesAsync();

        var client = new FakeClient
        {
            All = [Nav("copyA", "A/Al/01.flac"), Nav("copyB", "A/Al/01.flac")],
        };
        await Reconciler(db, client).ReconcileAllAsync(default);

        Assert.Contains("copyA", client.Starred);
        Assert.Contains("copyB", client.Starred);
    }

    // ── Immediate single-song push ───────────────────────────────────────────

    [Fact]
    public async Task PushSongAsync_AppliesCurrentLikeToNavidrome()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: true, mergeBase: null));
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "A/Al/01.flac")] };
        await Reconciler(db, client).PushSongAsync(1, default);

        Assert.Contains("nav1", client.Starred);
        Assert.True((await db.Songs.SingleAsync()).LikeLastSyncedValue);
    }

    [Fact]
    public async Task DisabledNavidrome_DoesNothing()
    {
        await using var db = CreateDb();
        db.Songs.Add(Song(1, destRel: "A/Al/01.flac", liked: true, mergeBase: null));
        await db.SaveChangesAsync();

        var client = new FakeClient { All = [Nav("nav1", "A/Al/01.flac")] };
        var reconciler = Reconciler(db, client, configured: false);
        await reconciler.ReconcileAllAsync(default);

        Assert.Empty(client.Ops);
        Assert.Equal(0, client.StarredCalls + client.SearchCalls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NavidromeLikeReconciler Reconciler(MusicHoarderDbContext db, FakeClient client, bool configured = true)
    {
        var nav = new NavidromeOptions
        {
            Enabled = configured,
            BaseUrl = configured ? "http://navidrome:4533" : "",
            Username = configured ? "admin" : "",
            Password = configured ? "pw" : "",
        };
        var enricher = new MusicEnricherOptions { SourceDirectory = SourceDir, DestinationDirectory = DestDir };
        return new NavidromeLikeReconciler(
            db, client, new OwnerLookupService(),
            new StaticMonitor<NavidromeOptions>(nav),
            new StaticMonitor<MusicEnricherOptions>(enricher),
            NullLogger<NavidromeLikeReconciler>.Instance);
    }

    private static SongMetadata Song(
        int id, string? sourceRel = null, string? destRel = null, string? mbid = null,
        string? artist = "Artist", string? title = "Title", int? duration = 200,
        bool liked = false, bool? mergeBase = null)
    {
        var song = new SongMetadata
        {
            Id = id,
            OwnerUserId = Owner,
            SourcePath = sourceRel is null ? $"{SourceDir}/song{id}.mp3" : $"{SourceDir}/{sourceRel}",
            FileSizeBytes = 1000,
            FileName = $"song{id}",
            Extension = ".flac",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Title = title,
            DurationSeconds = duration,
            MusicBrainzId = mbid,
            LikedAtUtc = liked ? DateTime.UtcNow : null,
            LikeLastSyncedValue = mergeBase,
            DestinationPath = destRel is null ? null : $"{DestDir}/{destRel}",
            LibraryBuildStatus = destRel is null ? LibraryBuildStatus.Pending : LibraryBuildStatus.Done,
        };
        return song;
    }

    private static NavidromeSong Nav(string id, string path, string? mbid = null, string? artist = null, string? title = null, int? dur = null)
        => new(id, title, artist, null, path, mbid, dur, null);

    private static MusicHoarderDbContext CreateDb()
        => new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeClient : INavidromeClient
    {
        public List<NavidromeSong> All { get; init; } = [];
        public HashSet<string> Starred { get; init; } = [];
        public List<(string Id, bool Star)> Ops { get; } = [];
        public int StarredCalls { get; private set; }
        public int SearchCalls { get; private set; }

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<NavidromeSong>> GetStarredSongsAsync(CancellationToken ct)
        {
            StarredCalls++;
            return Task.FromResult<IReadOnlyList<NavidromeSong>>(All.Where(s => Starred.Contains(s.Id)).ToList());
        }

        public Task<IReadOnlyList<NavidromeSong>> SearchSongsAsync(string query, int limit, CancellationToken ct)
        {
            SearchCalls++;
            return Task.FromResult<IReadOnlyList<NavidromeSong>>(All); // Find() filters by identity
        }

        public Task StarAsync(string songId, CancellationToken ct)
        {
            Ops.Add((songId, true));
            Starred.Add(songId);
            return Task.CompletedTask;
        }

        public Task UnstarAsync(string songId, CancellationToken ct)
        {
            Ops.Add((songId, false));
            Starred.Remove(songId);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
