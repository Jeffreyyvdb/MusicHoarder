using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class SyncEndpointsTests
{
    [Fact]
    public async Task Requeue_ReArmsSettledOutboxRows_LeavesActiveOnesAlone()
    {
        await using var db = NewContext();
        db.Songs.AddRange(Song(1), Song(2), Song(3), Song(4));
        db.TrackSyncStates.AddRange(
            State(1, TrackSyncStatus.Synced),
            State(2, TrackSyncStatus.SkippedRemoteBetter),
            State(3, TrackSyncStatus.Failed, attempts: 8, error: "parked"),
            State(4, TrackSyncStatus.Uploading));
        await db.SaveChangesAsync();

        var result = await SyncEndpoints.Requeue(PushOptions(), db, CancellationToken.None);

        Assert.Equal(202, GetStatusCode(result));
        var states = await db.TrackSyncStates.OrderBy(s => s.SongId).ToListAsync();
        Assert.Equal(TrackSyncStatus.Pending, states[0].Status);
        Assert.Equal(TrackSyncStatus.Pending, states[1].Status);
        Assert.Equal(TrackSyncStatus.Pending, states[2].Status);
        Assert.Equal(0, states[2].Attempts);           // parked row un-parked
        Assert.Null(states[2].LastError);
        Assert.Equal(TrackSyncStatus.Uploading, states[3].Status); // in-flight row untouched
    }

    [Fact]
    public async Task Requeue_NotPushMode_Conflicts()
    {
        await using var db = NewContext();

        var result = await SyncEndpoints.Requeue(
            Options(new SyncOptions { Mode = SyncMode.Receive }), db, CancellationToken.None);

        Assert.Equal(409, GetStatusCode(result));
    }

    // ── Prune duplicates (receive-side cleanup) ─────────────────────────────

    [Fact]
    public async Task PruneDuplicates_DryRun_CountsRedundantManagedCopies_WithoutTouchingAnything()
    {
        using var managed = new TempDir();
        await using var db = NewContext();
        var keeper = Fingerprinted(1, "/lib/original.flac", "FP1", extension: ".flac", bitrate: 900);
        var copy1 = await ManagedCopy(2, managed, "copy1.opus", "FP1");
        var copy2 = await ManagedCopy(3, managed, "copy2.opus", "FP1");
        db.Songs.AddRange(keeper, copy1, copy2);
        await db.SaveChangesAsync();

        var result = await SyncEndpoints.PruneDuplicates(
            ReceiveOptions(managed.Path), db, NullLogger<SyncEndpoints.SyncPruneLog>.Instance,
            apply: false, CancellationToken.None);

        Assert.Equal(2, Value<int>(result, "rows"));
        Assert.True(Value<bool>(result, "dryRun"));
        Assert.All(await db.Songs.ToListAsync(), s => Assert.Null(s.DeletedAtUtc));
        Assert.Equal(2, Directory.GetFiles(managed.Path, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task PruneDuplicates_Apply_SoftDeletesLosersAndRemovesTheirManagedFiles()
    {
        using var managed = new TempDir();
        await using var db = NewContext();
        var keeper = Fingerprinted(1, "/lib/original.flac", "FP1", extension: ".flac", bitrate: 900);
        var copy = await ManagedCopy(2, managed, "copy.opus", "FP1");
        db.Songs.AddRange(keeper, copy);
        await db.SaveChangesAsync();

        var result = await SyncEndpoints.PruneDuplicates(
            ReceiveOptions(managed.Path), db, NullLogger<SyncEndpoints.SyncPruneLog>.Instance,
            apply: true, CancellationToken.None);

        Assert.Equal(1, Value<int>(result, "rows"));
        Assert.Equal(1, Value<int>(result, "filesDeleted"));
        var rows = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Null(rows[0].DeletedAtUtc);       // keeper survives
        Assert.NotNull(rows[1].DeletedAtUtc);    // loser retired, row not removed
        Assert.False(File.Exists(copy.SourcePath));
    }

    [Fact]
    public async Task PruneDuplicates_NeverTouchesUnmanagedSourcesOrBuiltRows()
    {
        using var managed = new TempDir();
        await using var db = NewContext();
        var keeper = Fingerprinted(1, "/lib/original.flac", "FP1", extension: ".flac", bitrate: 900);

        // Same fingerprint, but the source lives outside the managed synced dir.
        var scannedElsewhere = Fingerprinted(2, "/lib/other-copy.flac", "FP1");

        // Same fingerprint, inside the managed dir, but it owns a destination file.
        var built = await ManagedCopy(3, managed, "built.opus", "FP1");
        built.MarkBuildDone("/dest/Artist/song.opus");

        db.Songs.AddRange(keeper, scannedElsewhere, built);
        await db.SaveChangesAsync();

        var result = await SyncEndpoints.PruneDuplicates(
            ReceiveOptions(managed.Path), db, NullLogger<SyncEndpoints.SyncPruneLog>.Instance,
            apply: true, CancellationToken.None);

        Assert.Equal(0, Value<int>(result, "rows"));
        Assert.All(await db.Songs.ToListAsync(), s => Assert.Null(s.DeletedAtUtc));
        Assert.True(File.Exists(built.SourcePath));
    }

    [Fact]
    public async Task PruneDuplicates_NoManagedDirectoryConfigured_Conflicts()
    {
        await using var db = NewContext();

        var result = await SyncEndpoints.PruneDuplicates(
            Options(new SyncOptions { Mode = SyncMode.Receive }), db,
            NullLogger<SyncEndpoints.SyncPruneLog>.Instance, apply: false, CancellationToken.None);

        Assert.Equal(409, GetStatusCode(result));
    }

    private static int? GetStatusCode(Microsoft.AspNetCore.Http.IResult result)
        => (int?)result.GetType().GetProperty("StatusCode")?.GetValue(result);

    /// <summary>Reads a property off the anonymous-typed payload of an Ok(...) result.</summary>
    private static T Value<T>(Microsoft.AspNetCore.Http.IResult result, string name)
    {
        var payload = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return (T)payload.GetType().GetProperty(name)!.GetValue(payload)!;
    }

    private static IOptionsMonitor<SyncOptions> ReceiveOptions(string syncedDir) => Options(new SyncOptions
    {
        Mode = SyncMode.Receive,
        ApiKey = new string('k', 40),
        SyncedSourceDirectory = syncedDir,
    });

    private static SongMetadata Fingerprinted(
        int id, string path, string fingerprint, string extension = ".opus", int? bitrate = 128)
    {
        var song = Song(id);
        song.SourcePath = path;
        song.FileName = Path.GetFileName(path);
        song.Extension = extension;
        song.Bitrate = bitrate;
        song.Fingerprint = fingerprint;
        song.Artist = "Artist";
        song.Title = "Song";
        return song;
    }

    private static async Task<SongMetadata> ManagedCopy(
        int id, TempDir managed, string fileName, string fingerprint)
    {
        var path = Path.Combine(managed.Path, "Artist", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[16]);
        return Fingerprinted(id, path.Replace('\\', '/'), fingerprint);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mh-prune-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private static IOptionsMonitor<SyncOptions> PushOptions() => Options(new SyncOptions
    {
        Mode = SyncMode.Push,
        ApiKey = new string('k', 40),
        RemoteBaseUrl = "https://public.example",
    });

    private static IOptionsMonitor<SyncOptions> Options(SyncOptions value) =>
        new StaticOptionsMonitor<SyncOptions>(value);

    private static SongMetadata Song(int id) => new()
    {
        Id = id,
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = $"/src/{id}.opus",
        FileSizeBytes = 1000,
        FileName = $"{id}.opus",
        Extension = ".opus",
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
    };

    private static TrackSyncState State(
        int songId, TrackSyncStatus status, int attempts = 0, string? error = null) => new()
    {
        SongId = songId,
        Status = status,
        Attempts = attempts,
        LastError = error,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
