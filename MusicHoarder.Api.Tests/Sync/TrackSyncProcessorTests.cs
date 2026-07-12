using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Tests.Sync;

public class TrackSyncProcessorTests : IDisposable
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private readonly List<string> tempFiles = [];

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    // ── Sweep predicate ─────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_PicksBuiltTracksWithoutStateRow_SkipsIneligibleSongs()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(BuiltSong(1));
        var notBuilt = BuiltSong(2);
        notBuilt.ResetLibraryBuild();
        db.Songs.Add(notBuilt);
        var dup = BuiltSong(3);
        dup.MarkAsDuplicate(1);
        db.Songs.Add(dup);
        var deleted = BuiltSong(4);
        deleted.SoftDelete();
        db.Songs.Add(deleted);
        var synthetic = BuiltSong(5);
        synthetic.IsSynthetic = true;
        db.Songs.Add(synthetic);
        var demo = BuiltSong(6);
        demo.OwnerUserId = WellKnownUsers.DemoId;
        db.Songs.Add(demo);
        await db.SaveChangesAsync();

        var candidates = await CreateProcessor(db, new FakePushClient()).FindSweepCandidatesAsync(100, default);

        Assert.Equal([1], candidates);
    }

    [Fact]
    public async Task Sweep_SkipsSyncedRows_ReArmsOnFingerprintChange()
    {
        await using var db = CreateDbContext();
        var unchanged = BuiltSong(1, fingerprint: "FP1");
        var upgraded = BuiltSong(2, fingerprint: "FP2-new");
        db.Songs.AddRange(unchanged, upgraded);
        db.TrackSyncStates.AddRange(
            SyncedState(1, "FP1"),
            SyncedState(2, "FP2-old"));
        await db.SaveChangesAsync();

        var candidates = await CreateProcessor(db, new FakePushClient()).FindSweepCandidatesAsync(100, default);

        Assert.Equal([2], candidates);
    }

    [Fact]
    public async Task Sweep_RetriesFailedAfterBackoff_ParksAfterMaxAttempts()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(BuiltSong(1), BuiltSong(2), BuiltSong(3));
        db.TrackSyncStates.AddRange(
            FailedState(1, attempts: 2, nextAttempt: DateTime.UtcNow.AddMinutes(-1)),  // retryable
            FailedState(2, attempts: 2, nextAttempt: DateTime.UtcNow.AddMinutes(10)),  // backoff not elapsed
            FailedState(3, attempts: 8, nextAttempt: null));                            // parked (max attempts)
        await db.SaveChangesAsync();

        var candidates = await CreateProcessor(db, new FakePushClient()).FindSweepCandidatesAsync(100, default);

        Assert.Equal([1], candidates);
    }

    // ── Per-song state machine ──────────────────────────────────────────────

    [Fact]
    public async Task Process_RemoteSameOrBetter_SkipsWithoutUploading()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(BuiltSong(1, fingerprint: "FP1", destinationPath: MakeTempFile()));
        await db.SaveChangesAsync();
        var client = new FakePushClient
        {
            CheckResult = new SyncCheckResponse(SyncVerdict.PresentSameOrBetter, 42, 400_900, "fingerprint"),
        };

        await CreateProcessor(db, client).ProcessSongAsync(1, default);

        var state = await db.TrackSyncStates.SingleAsync();
        Assert.Equal(TrackSyncStatus.SkippedRemoteBetter, state.Status);
        Assert.Equal("FP1", state.SyncedFingerprint);
        Assert.Equal(42, state.RemoteSongId);
        Assert.Equal(0, client.Uploads);
    }

    [Fact]
    public async Task Process_NotPresent_UploadsDestinationFileAndMarksSynced()
    {
        await using var db = CreateDbContext();
        var dest = MakeTempFile();
        db.Songs.Add(BuiltSong(1, fingerprint: "FP1", destinationPath: dest));
        await db.SaveChangesAsync();
        var client = new FakePushClient
        {
            CheckResult = new SyncCheckResponse(SyncVerdict.NotPresent, null, null, null),
            UploadResult = new SyncUploadResponse(SyncUploadOutcome.Created, 55, 400_900),
        };

        await CreateProcessor(db, client).ProcessSongAsync(1, default);

        var state = await db.TrackSyncStates.SingleAsync();
        Assert.Equal(TrackSyncStatus.Synced, state.Status);
        Assert.Equal("FP1", state.SyncedFingerprint);
        Assert.Equal(55, state.RemoteSongId);
        Assert.Equal(1, client.Uploads);
        Assert.Equal(dest, client.LastUploadedFile); // the built artifact, not the source
    }

    [Fact]
    public async Task Process_TransportFailure_MarksFailedWithBackoff()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(BuiltSong(1, destinationPath: MakeTempFile()));
        await db.SaveChangesAsync();
        var client = new FakePushClient { Throw = new HttpRequestException("remote unreachable") };

        await CreateProcessor(db, client).ProcessSongAsync(1, default);

        var state = await db.TrackSyncStates.SingleAsync();
        Assert.Equal(TrackSyncStatus.Failed, state.Status);
        Assert.Equal(1, state.Attempts);
        Assert.NotNull(state.NextAttemptAtUtc);
        Assert.Contains("unreachable", state.LastError);
    }

    [Fact]
    public async Task Process_MissingFilesOnDisk_FailsWithoutCalling()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(BuiltSong(1, destinationPath: "/nonexistent/dest.flac", sourcePath: "/nonexistent/src.flac"));
        await db.SaveChangesAsync();
        var client = new FakePushClient();

        await CreateProcessor(db, client).ProcessSongAsync(1, default);

        var state = await db.TrackSyncStates.SingleAsync();
        Assert.Equal(TrackSyncStatus.Failed, state.Status);
        Assert.Equal(0, client.Checks);
    }

    [Fact]
    public async Task Process_SongNoLongerEligible_NoOp()
    {
        await using var db = CreateDbContext();
        var song = BuiltSong(1);
        song.SoftDelete();
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        await CreateProcessor(db, new FakePushClient()).ProcessSongAsync(1, default);

        Assert.Empty(await db.TrackSyncStates.ToListAsync());
    }

    // ── Like propagation ────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_ReArmsSyncedRow_WhenLikeChanged()
    {
        await using var db = CreateDbContext();
        var liked = BuiltSong(1, fingerprint: "FP");
        liked.LikedAtUtc = DateTime.UtcNow;              // liked now, but state says not-synced-liked
        var unchanged = BuiltSong(2, fingerprint: "FP");  // not liked, state says not-synced-liked
        db.Songs.AddRange(liked, unchanged);
        db.TrackSyncStates.AddRange(
            SyncedState(1, "FP"),   // SyncedLiked defaults false → diverges from liked=true → re-arm
            SyncedState(2, "FP"));  // false == false → no re-arm
        await db.SaveChangesAsync();

        var candidates = await CreateProcessor(db, new FakePushClient()).FindSweepCandidatesAsync(100, default);

        Assert.Equal([1], candidates);
    }

    [Fact]
    public async Task Process_FilePresent_LikeChanged_PushesLikeOnly_NoUpload()
    {
        await using var db = CreateDbContext();
        var song = BuiltSong(1, destinationPath: MakeTempFile());
        song.LikedAtUtc = DateTime.UtcNow;
        db.Songs.Add(song);
        db.TrackSyncStates.Add(SyncedState(1, "FP")); // SyncedLiked=false, remote has the file
        await db.SaveChangesAsync();
        var client = new FakePushClient { CheckResult = new SyncCheckResponse(SyncVerdict.PresentSameOrBetter, 42, 10, "mbid") };

        await CreateProcessor(db, client).ProcessSongAsync(1, default);

        Assert.Equal(0, client.Uploads);                 // no file re-upload
        var push = Assert.Single(client.LikePushes);
        Assert.NotNull(push.LikedAtUtc);                 // like carried
        var state = await db.TrackSyncStates.SingleAsync();
        Assert.True(state.SyncedLiked);                  // advanced only after the push succeeded
    }

    [Fact]
    public async Task Process_FilePresent_LikeUnchanged_DoesNotPushLike()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(BuiltSong(1, destinationPath: MakeTempFile())); // not liked
        var state = SyncedState(1, "FP");
        state.SyncedLiked = false;  // matches "not liked"
        db.TrackSyncStates.Add(state);
        await db.SaveChangesAsync();
        var client = new FakePushClient { CheckResult = new SyncCheckResponse(SyncVerdict.PresentSameOrBetter, 42, 10, "mbid") };

        await CreateProcessor(db, client).ProcessSongAsync(1, default);

        Assert.Empty(client.LikePushes);
    }

    [Fact]
    public async Task Process_Upload_CarriesLike_AndRecordsSyncedLiked()
    {
        await using var db = CreateDbContext();
        var dest = MakeTempFile();
        var song = BuiltSong(1, destinationPath: dest);
        song.LikedAtUtc = DateTime.UtcNow;
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        var client = new FakePushClient
        {
            CheckResult = new SyncCheckResponse(SyncVerdict.NotPresent, null, null, null),
            UploadResult = new SyncUploadResponse(SyncUploadOutcome.Created, 55, 10),
        };

        await CreateProcessor(db, client).ProcessSongAsync(1, default);

        Assert.NotNull(client.LastUploadedPayload!.LikedAtUtc); // like rides along in the file push
        Assert.Empty(client.LikePushes);                        // no separate like call needed
        Assert.True((await db.TrackSyncStates.SingleAsync()).SyncedLiked);
    }

    // ── Enqueuer gate ───────────────────────────────────────────────────────

    [Fact]
    public void Enqueuer_DropsDemoAndUnconfigured()
    {
        var channel = new TrackSyncChannel();

        var off = new TrackSyncEnqueuer(channel, StaticOptions(new SyncOptions { Mode = SyncMode.Off }));
        off.TryEnqueue(1, Owner);
        Assert.False(channel.Reader.TryRead(out _));

        var on = new TrackSyncEnqueuer(channel, StaticOptions(PushOptions()));
        on.TryEnqueue(2, WellKnownUsers.DemoId);
        Assert.False(channel.Reader.TryRead(out _));

        on.TryEnqueue(3, Owner);
        Assert.True(channel.Reader.TryRead(out var id));
        Assert.Equal(3, id);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SongMetadata BuiltSong(
        int id, string? fingerprint = "FP", string? destinationPath = "/dest/file.opus", string? sourcePath = null)
    {
        var song = new SongMetadata
        {
            Id = id,
            OwnerUserId = Owner,
            SourcePath = sourcePath ?? $"/src/{id}.opus",
            FileSizeBytes = 1000,
            FileName = $"{id}.opus",
            Extension = ".opus",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Fingerprint = fingerprint,
            Bitrate = 128,
            DurationMs = 200_000,
            DurationSeconds = 200,
            Artist = "Artist",
            Title = "Song",
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        song.MarkBuildDone(destinationPath!);
        return song;
    }

    private static TrackSyncState SyncedState(int songId, string fingerprint) => new()
    {
        SongId = songId,
        Status = TrackSyncStatus.Synced,
        SyncedFingerprint = fingerprint,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static TrackSyncState FailedState(int songId, int attempts, DateTime? nextAttempt) => new()
    {
        SongId = songId,
        Status = TrackSyncStatus.Failed,
        Attempts = attempts,
        NextAttemptAtUtc = nextAttempt,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static SyncOptions PushOptions() => new()
    {
        Mode = SyncMode.Push,
        ApiKey = new string('k', 40),
        RemoteBaseUrl = "https://public.example",
        MaxAttempts = 8,
        RetryBaseDelaySeconds = 30,
    };

    private string MakeTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mh-syncpush-{Guid.NewGuid():N}.opus").Replace('\\', '/');
        File.WriteAllBytes(path, new byte[32]);
        tempFiles.Add(path);
        return path;
    }

    private static TrackSyncProcessor CreateProcessor(MusicHoarderDbContext db, FakePushClient client) =>
        new(db, client, new OwnerLookupService(), StaticOptions(PushOptions()),
            NullLogger<TrackSyncProcessor>.Instance);

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static IOptionsMonitor<SyncOptions> StaticOptions(SyncOptions value) =>
        new StaticOptionsMonitor<SyncOptions>(value);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakePushClient : ISyncPushClient
    {
        public SyncCheckResponse? CheckResult { get; set; }
        public SyncUploadResponse? UploadResult { get; set; }
        public Exception? Throw { get; set; }
        public int Checks { get; private set; }
        public int Uploads { get; private set; }
        public string? LastUploadedFile { get; private set; }
        public SyncTrackPayload? LastUploadedPayload { get; private set; }
        public List<SyncLikeRequest> LikePushes { get; } = [];

        public Task<SyncCheckResponse?> CheckAsync(SyncCheckRequest request, CancellationToken ct)
        {
            Checks++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(CheckResult);
        }

        public Task<SyncUploadResponse?> UploadAsync(SyncTrackPayload payload, string filePath, CancellationToken ct)
        {
            Uploads++;
            if (Throw is not null) throw Throw;
            LastUploadedFile = filePath;
            LastUploadedPayload = payload;
            return Task.FromResult(UploadResult);
        }

        public Task<SyncLikeResponse?> PushLikeAsync(SyncLikeRequest request, CancellationToken ct)
        {
            LikePushes.Add(request);
            if (Throw is not null) throw Throw;
            return Task.FromResult<SyncLikeResponse?>(new SyncLikeResponse(Matched: true, SongId: 99));
        }
    }
}
