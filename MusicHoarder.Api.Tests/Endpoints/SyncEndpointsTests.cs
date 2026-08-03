using Microsoft.EntityFrameworkCore;
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

    private static int? GetStatusCode(Microsoft.AspNetCore.Http.IResult result)
        => (int?)result.GetType().GetProperty("StatusCode")?.GetValue(result);

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
