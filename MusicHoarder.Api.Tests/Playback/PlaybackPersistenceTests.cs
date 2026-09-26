using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Playback;
using MusicHoarder.Api.Tests.Auth;
using static MusicHoarder.Api.Tests.Playback.PlaybackTestKit;

namespace MusicHoarder.Api.Tests.Playback;

/// <summary>
/// "Always remember the last session": what reaches <see cref="PlaybackSession"/>, when, and what
/// a restarted API makes of it.
/// </summary>
public class PlaybackPersistenceTests
{
    [Fact]
    public async Task A_session_round_trips_through_the_table()
    {
        var store = NewEfStore(out _);
        var saved = Record(Alice, songId: 123, queue: [120, 123, 131], queueIndex: 1, isPlaying: true);

        await store.SaveAsync([saved], CancellationToken.None);
        var loaded = await store.LoadAsync(Alice, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(saved with { Queue = loaded.Queue }, loaded);
        Assert.Equal([120, 123, 131], loaded.Queue);
    }

    [Fact]
    public async Task Saving_again_updates_the_one_row_per_user()
    {
        var store = NewEfStore(out var services);

        await store.SaveAsync([Record(Alice, songId: 1), Record(Bob, songId: 50)], CancellationToken.None);
        await store.SaveAsync([Record(Alice, songId: 2, version: 9)], CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var rows = await db.PlaybackSessions.IgnoreQueryFilters().OrderBy(r => r.SongId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal((2, 9L), (rows[0].SongId, rows[0].Version));
        Assert.Equal(Alice, rows[0].OwnerUserId);
        Assert.Equal("[1,2,3]", rows[0].QueueJson);
        Assert.Equal(50, rows[1].SongId);
    }

    [Fact]
    public async Task The_store_reads_past_the_callers_query_filter()
    {
        // The coordinator can load inside another account's request (the scope inherits its
        // HttpContext), so it must never rely on the ambient per-user filter.
        var store = NewEfStore(out _, accessor: new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser));

        await store.SaveAsync([Record(Alice, songId: 7)], CancellationToken.None);

        Assert.Equal(7, (await store.LoadAsync(Alice, CancellationToken.None))!.SongId);
        Assert.Null(await store.LoadAsync(Bob, CancellationToken.None));
    }

    [Fact]
    public async Task The_table_is_private_to_each_user_under_the_query_filter()
    {
        var store = NewEfStore(out var services);
        await store.SaveAsync([Record(Alice, songId: 1), Record(Bob, songId: 50)], CancellationToken.None);

        var options = services.GetRequiredService<DbContextOptions<MusicHoarderDbContext>>();
        await using var asBob = new MusicHoarderDbContext(
            options, new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser));

        Assert.Equal(50, (await asBob.PlaybackSessions.SingleAsync()).SongId);
    }

    [Fact]
    public async Task A_corrupt_queue_degrades_to_the_current_song()
    {
        var store = NewEfStore(out var services);
        await store.SaveAsync([Record(Alice, songId: 5)], CancellationToken.None);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var row = await db.PlaybackSessions.IgnoreQueryFilters().SingleAsync();
            row.QueueJson = "not json";
            await db.SaveChangesAsync();
        }

        var loaded = (await store.LoadAsync(Alice, CancellationToken.None))!;

        Assert.Equal([5], loaded.Queue);
        Assert.Equal(0, loaded.QueueIndex);
    }

    [Fact]
    public async Task A_remembered_session_comes_back_paused_on_the_same_device()
    {
        var store = NewEfStore(out _);
        await store.SaveAsync(
            [Record(Alice, songId: 123, queue: [120, 123, 131], queueIndex: 1, positionMs: 83_000,
                isPlaying: true, version: 42, activeDeviceId: Phone, activeDeviceName: "Safari on iPhone")],
            CancellationToken.None);

        // A fresh API: nothing in memory, and the phone is not connected.
        var kit = new PlaybackTestKit(store);
        var remembered = (await kit.Snapshot(Alice)).Session!;

        Assert.False(remembered.IsPlaying);
        Assert.False(remembered.Live);
        Assert.Equal(83_000, remembered.PositionMs);
        Assert.Equal(Phone, remembered.ActiveDeviceId);
        Assert.Equal("Safari on iPhone", remembered.ActiveDeviceName);
        Assert.Equal(42, remembered.Version);
        Assert.Equal([120, 123, 131], remembered.Queue);

        // Its stream coming back proves nothing (a phone restarted with nothing loaded does just
        // that) ...
        using var phone = await kit.Connect(Alice, Phone);
        var reconnected = await kit.Snapshot(Alice);
        Assert.False(reconnected.Session!.Live);
        Assert.Equal(42, reconnected.Session.Version);

        // ... although it is there, listed and able to take the session when handed it.
        Assert.Contains(reconnected.Devices, d => d.DeviceId == Phone && d.IsActive && d.Online);
        using var mac = await kit.Connect(Alice, Mac);
        Assert.Null((await kit.Command(Alice, from: Mac, "transfer", target: Phone)).Error);
        Assert.False((await kit.Snapshot(Alice)).Session!.Live);

        // ... but once it reports, it still holds the session, so it simply carries on.
        var carriedOn = await kit.Accept(
            Alice, Report(Phone, songId: 123, queueIndex: 1, positionMs: 90_000, isPlaying: true));
        Assert.True(carriedOn.Accepted);
        Assert.True(carriedOn.Session!.IsPlaying);
        Assert.True(carriedOn.Session.Live);
        Assert.True(carriedOn.Session.Version > 42);
    }

    [Fact]
    public async Task Pressing_play_elsewhere_adopts_the_remembered_session()
    {
        var store = new InMemoryPlaybackSessionStore();
        store.Rows[Alice] = Record(Alice, songId: 123, queue: [120, 123, 131], queueIndex: 1, activeDeviceId: Phone);
        var kit = new PlaybackTestKit(store);
        using var mac = await kit.Connect(Alice, Mac);
        Assert.Equal(Phone, mac.Snapshot.Session!.ActiveDeviceId);

        var adopted = await kit.Accept(Alice, Report(
            Mac, claim: true, songId: 123, queue: [120, 123, 131], queueIndex: 1, positionMs: 83_000));

        Assert.Equal(Mac, adopted.Session!.ActiveDeviceId);
        Assert.True(adopted.Session.Live);
    }

    [Fact]
    public async Task Only_changed_sessions_are_written()
    {
        var store = new InMemoryPlaybackSessionStore();
        var kit = new PlaybackTestKit(store);
        var flusher = Flusher(kit);
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Snapshot(Bob); // loaded, but Bob never played anything

        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);
        Assert.Equal(0, store.Saves);

        await kit.Claim(Alice, Mac, positionMs: 10_000);
        kit.Advance(3);
        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);
        Assert.Equal(1, store.Saves);
        var row = store.Rows[Alice];
        Assert.Equal(13_000, row.PositionMs); // projected to the moment of writing
        Assert.Equal(Mac, row.ActiveDeviceId);
        Assert.Equal([1, 2, 3], row.Queue);
        Assert.False(store.Rows.ContainsKey(Bob));

        // Nothing changed since: nothing to write, however long it keeps playing.
        kit.Advance(3);
        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task A_failed_write_is_retried_on_the_next_tick()
    {
        var store = new InMemoryPlaybackSessionStore();
        var kit = new PlaybackTestKit(store);
        var flusher = Flusher(kit);
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);

        store.FailNextSave = true;
        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);
        Assert.Empty(store.Rows);

        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);
        Assert.True(store.Rows.ContainsKey(Alice));
    }

    [Fact]
    public async Task Shutdown_writes_every_playing_session_at_its_latest_position()
    {
        var store = new InMemoryPlaybackSessionStore();
        var kit = new PlaybackTestKit(store);
        var flusher = Flusher(kit);
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac, positionMs: 10_000);
        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);

        kit.Advance(20);
        await flusher.StartAsync(CancellationToken.None);

        // By the time the host stops this service, its shared stop token has usually run out.
        await flusher.StopAsync(new CancellationToken(canceled: true));

        Assert.Equal(2, store.Saves);
        Assert.Equal(30_000, store.Rows[Alice].PositionMs);
    }

    [Fact]
    public async Task The_remembered_position_survives_its_device_reconnecting_without_a_report()
    {
        var store = new InMemoryPlaybackSessionStore();
        var kit = new PlaybackTestKit(store);
        var flusher = Flusher(kit);
        var phone = await kit.Connect(Alice, Phone);
        await kit.Claim(Alice, Phone, positionMs: 60_000, durationMs: 240_000);

        kit.Advance(5);
        phone.Dispose(); // the app was killed
        kit.PastReconnectGrace();
        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);
        Assert.Equal(65_000, store.Rows[Alice].PositionMs);

        kit.Advance(30);
        using var reopened = await kit.Connect(Alice, Phone); // with nothing loaded
        kit.Advance(60);
        kit.Coordinator.Sweep();
        await flusher.FlushAsync(includePlaying: true, CancellationToken.None);

        var row = store.Rows[Alice];
        Assert.False(row.IsPlaying);
        Assert.Equal(65_000, row.PositionMs);
    }

    [Fact]
    public async Task The_sweep_marks_a_detached_session_for_writing()
    {
        var store = new InMemoryPlaybackSessionStore();
        var kit = new PlaybackTestKit(store);
        var flusher = Flusher(kit);
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac, positionMs: 0);
        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);

        kit.Advance(80); // the Mac stopped reporting
        kit.Coordinator.Sweep();
        await flusher.FlushAsync(includePlaying: false, CancellationToken.None);

        var row = store.Rows[Alice];
        Assert.False(row.IsPlaying);
        Assert.Equal(75_000, row.PositionMs);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static PlaybackSessionFlushService Flusher(PlaybackTestKit kit) =>
        new(kit.Coordinator, kit.Store, kit.Clock, NullLogger<PlaybackSessionFlushService>.Instance);

    private static EfPlaybackSessionStore NewEfStore(
        out ServiceProvider services, ICurrentUserAccessor? accessor = null)
    {
        var collection = new ServiceCollection();
        var database = Guid.NewGuid().ToString("N");
        collection.AddDbContext<MusicHoarderDbContext>(o => o.UseInMemoryDatabase(database));
        if (accessor is not null) collection.AddSingleton(accessor);
        services = collection.BuildServiceProvider();
        return new EfPlaybackSessionStore(services.GetRequiredService<IServiceScopeFactory>());
    }

    private static PlaybackSessionRecord Record(
        Guid owner,
        int songId,
        int[]? queue = null,
        int queueIndex = 0,
        long positionMs = 1_000,
        bool isPlaying = false,
        long version = 1,
        string? activeDeviceId = Mac,
        string? activeDeviceName = "Safari on Mac") =>
        new(
            owner,
            songId,
            queue ?? [1, 2, 3],
            queueIndex,
            positionMs,
            DurationMs: 215_000,
            isPlaying,
            PlaybackRate: 1.0,
            RadioSeedId: 120,
            Shuffle: true,
            Title: "Nightswim",
            Artist: "R.E.M.",
            Album: "Automatic for the People",
            activeDeviceId,
            activeDeviceName,
            version,
            UpdatedAtUtc: new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc));
}
