using MusicHoarder.Api.Playback;
using static MusicHoarder.Api.Tests.Playback.PlaybackTestKit;

namespace MusicHoarder.Api.Tests.Playback;

/// <summary>
/// The invariant of playback sync: one session per account, at most one active device, and a
/// claim is the only thing that moves it. Plus what every device is told, and when.
/// </summary>
public class PlaybackCoordinatorTests
{
    // ── One active device ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_claim_makes_the_device_active_and_supersedes_the_previous_one()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);

        var first = await kit.Claim(Alice, Mac, positionMs: 30_000);
        Assert.True(first.Accepted);
        Assert.Equal(Mac, first.Session!.ActiveDeviceId);
        Assert.Equal("Safari on Mac", first.Session.ActiveDeviceName);
        Assert.True(first.Session.Live);
        Assert.True(first.Session.IsPlaying);
        await Drain(mac);

        var second = await kit.Claim(Alice, Phone);
        Assert.True(second.Accepted);
        Assert.Equal(Phone, second.Session!.ActiveDeviceId);

        // The Mac sees the session move, which is its cue to pause by itself.
        var seenByMac = Sessions(await Drain(mac)).Last();
        Assert.Equal(Phone, seenByMac.ActiveDeviceId);
        Assert.Equal("Safari on iPhone", seenByMac.ActiveDeviceName);

        // And if it missed that event, its next report tells it.
        var stale = await kit.Accept(Alice, Report(Mac, songId: 3, positionMs: 45_000));
        Assert.False(stale.Accepted);
        Assert.Equal(Phone, stale.Session!.ActiveDeviceId);
        Assert.Equal(2, stale.Session.SongId);
    }

    [Fact]
    public async Task The_active_device_updates_the_session_without_claiming()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);

        var next = await kit.Accept(Alice, Report(Mac, songId: 3, queueIndex: 2, positionMs: 0));

        Assert.True(next.Accepted);
        Assert.Equal(3, next.Session!.SongId);
        Assert.Equal(2, next.Session.QueueIndex);
        Assert.Equal([1, 2, 3], next.Session.Queue); // "queue: null" = unchanged
        Assert.Equal("Song 3", next.Session.Title);
    }

    [Fact]
    public async Task A_report_from_a_device_that_is_not_active_is_ignored_but_keeps_it_reachable()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);
        using var tablet = await kit.Connect(Alice, Tablet);
        await kit.Claim(Alice, Mac);

        kit.Advance(60);
        await kit.Accept(Alice, Report(Mac, songId: 2, queueIndex: 1)); // heartbeat
        var ignored = await kit.Accept(Alice, Report(Phone, songId: 99, queueIndex: 0, positionMs: 1));

        Assert.False(ignored.Accepted);
        Assert.Equal(2, ignored.Session!.SongId);
        Assert.Equal(Mac, ignored.Session.ActiveDeviceId);

        // 100 s after connecting: the phone was heard 40 s ago, the tablet not since it connected.
        kit.Advance(40);
        await kit.Accept(Alice, Report(Mac, songId: 2, queueIndex: 1));
        var devices = (await kit.Snapshot(Alice)).Devices;
        Assert.Contains(devices, d => d.DeviceId == Phone && d.Online);
        Assert.DoesNotContain(devices, d => d.DeviceId == Tablet);
    }

    [Fact]
    public async Task A_claim_requires_the_queue()
    {
        var kit = new PlaybackTestKit();

        var error = await kit.Reject(Alice, Report(Mac, claim: true, queue: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("queue_required", error.Code);
    }

    [Fact]
    public async Task A_claim_that_is_paused_still_takes_the_session()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);

        var response = await kit.Claim(Alice, Mac, positionMs: 5_000, isPlaying: false);

        Assert.True(response.Accepted);
        Assert.Equal(Mac, response.Session!.ActiveDeviceId);
        Assert.False(response.Session.IsPlaying);
    }

    [Fact]
    public async Task InResponseTo_is_recorded_only_when_the_report_is_accepted()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);
        await kit.Claim(Alice, Mac);

        var ignored = await kit.Accept(Alice, Report(Phone, inResponseTo: "cmd-from-phone"));
        Assert.Null(ignored.Session!.LastCommandId);

        var acked = await kit.Accept(Alice, Report(Mac, isPlaying: false, inResponseTo: "cmd-for-mac"));
        Assert.Equal("cmd-for-mac", acked.Session!.LastCommandId);

        // A later report that acknowledges nothing leaves the last acknowledgement standing.
        var heartbeat = await kit.Accept(Alice, Report(Mac, isPlaying: false));
        Assert.Equal("cmd-for-mac", heartbeat.Session!.LastCommandId);
    }

    // ── Commands ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_transfer_does_not_move_the_session_until_the_target_claims()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);
        await kit.Claim(Alice, Mac, positionMs: 60_000);
        await Drain(mac);
        await Drain(phone);

        var sent = await kit.Command(Alice, from: Mac, "transfer", target: Phone);
        Assert.Null(sent.Error);

        // The Mac keeps playing until the phone actually plays: no gap, no silence if it fails.
        var meanwhile = (await kit.Snapshot(Alice)).Session!;
        Assert.Equal(Mac, meanwhile.ActiveDeviceId);
        Assert.True(meanwhile.IsPlaying);

        var command = Assert.Single(Commands(await Drain(phone)));
        Assert.Equal("transfer", command.Command);
        Assert.Equal(sent.Value!.CommandId, command.CommandId);
        Assert.Equal(Mac, command.FromDeviceId);
        Assert.Equal("Safari on Mac", command.FromDeviceName);
        Assert.Empty(Commands(await Drain(mac)));

        var claimed = await kit.Claim(Alice, Phone, positionMs: 61_000, inResponseTo: command.CommandId);
        Assert.Equal(Phone, claimed.Session!.ActiveDeviceId);
        Assert.Equal(command.CommandId, claimed.Session.LastCommandId);
    }

    [Fact]
    public async Task A_command_reaches_only_the_target_devices_own_streams()
    {
        var kit = new PlaybackTestKit();
        using var macTab = await kit.Connect(Alice, Mac);
        using var macSecondStream = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);
        using var tablet = await kit.Connect(Alice, Tablet);
        using var otherAccount = await kit.Connect(Bob, Mac);
        await kit.Claim(Alice, Mac);
        foreach (var s in new[] { macTab, macSecondStream, phone, tablet, otherAccount }) await Drain(s);

        var sent = await kit.Command(Alice, from: Phone, "seek", positionMs: 42_000);
        Assert.Null(sent.Error);

        foreach (var stream in new[] { macTab, macSecondStream })
        {
            var command = Assert.Single(Commands(await Drain(stream)));
            Assert.Equal("seek", command.Command);
            Assert.Equal(42_000, command.PositionMs);
            Assert.Equal(Phone, command.FromDeviceId);
        }
        Assert.Empty(await Drain(phone));
        Assert.Empty(await Drain(tablet));
        Assert.Empty(await Drain(otherAccount));
    }

    [Fact]
    public async Task A_command_without_a_session_is_not_found()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);

        var outcome = await kit.Command(Alice, from: Phone, "pause");

        Assert.Equal(404, outcome.Error!.StatusCode);
        Assert.Equal("no_session", outcome.Error.Code);
    }

    [Fact]
    public async Task A_command_naming_a_device_that_is_not_active_conflicts()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);
        await kit.Claim(Alice, Mac);

        var outcome = await kit.Command(Alice, from: Mac, "pause", target: Phone);

        Assert.Equal(409, outcome.Error!.StatusCode);
        Assert.Equal("not_active_device", outcome.Error.Code);
    }

    [Fact]
    public async Task A_command_to_an_unreachable_device_conflicts()
    {
        var kit = new PlaybackTestKit();
        var mac = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);
        await kit.Claim(Alice, Mac);
        mac.Dispose(); // the tab closed
        kit.PastReconnectGrace();

        var pause = await kit.Command(Alice, from: Phone, "pause");
        Assert.Equal(409, pause.Error!.StatusCode);
        Assert.Equal("device_offline", pause.Error.Code);

        var transfer = await kit.Command(Alice, from: Phone, "transfer", target: Tablet);
        Assert.Equal(409, transfer.Error!.StatusCode);
        Assert.Equal("device_offline", transfer.Error.Code);
    }

    [Theory]
    [InlineData("shuffle", PlaybackTestKit.Mac, null, null, "invalid_command")]
    [InlineData(null, PlaybackTestKit.Mac, null, null, "invalid_command")]
    [InlineData("seek", PlaybackTestKit.Mac, null, null, "position_required")]
    [InlineData("transfer", PlaybackTestKit.Mac, null, null, "target_required")]
    [InlineData("pause", "bad id", null, null, "invalid_device_id")]
    [InlineData("pause", PlaybackTestKit.Mac, "x", null, "invalid_device_id")]
    public async Task A_malformed_command_is_a_bad_request(
        string? command, string from, string? target, long? positionMs, string code)
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);

        var outcome = await kit.Command(Alice, from, command!, target, positionMs);

        Assert.Equal(400, outcome.Error!.StatusCode);
        Assert.Equal(code, outcome.Error.Code);
    }

    // ── Reports that are rejected outright ──────────────────────────────────────────────────

    [Theory]
    [InlineData("bad id", "web", 1, 1, 0, "invalid_device_id")]
    [InlineData(PlaybackTestKit.Mac, "ios", 1, 1, 0, "invalid_client")]
    [InlineData(PlaybackTestKit.Mac, null, 1, 1, 0, "invalid_client")]
    [InlineData(PlaybackTestKit.Mac, "web", null, 1, 0, "song_required")]
    [InlineData(PlaybackTestKit.Mac, "web", 0, 1, 0, "song_required")]
    [InlineData(PlaybackTestKit.Mac, "web", 1, 2, 2, "invalid_queue_index")]
    [InlineData(PlaybackTestKit.Mac, "web", 1, 2, -1, "invalid_queue_index")]
    [InlineData(PlaybackTestKit.Mac, "web", 1, 0, 0, "invalid_queue_index")]
    public async Task A_malformed_report_is_a_bad_request(
        string deviceId, string? client, int? songId, int queueLength, int queueIndex, string code)
    {
        var kit = new PlaybackTestKit();
        var report = Report(
            deviceId, claim: true, songId: songId, queue: [.. Enumerable.Range(1, queueLength)],
            queueIndex: queueIndex, client: client);

        var error = await kit.Reject(Alice, report);

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task An_unchanged_queue_still_bounds_the_index()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac); // queue [1, 2, 3]

        var error = await kit.Reject(Alice, Report(Mac, queue: null, queueIndex: 3));

        Assert.Equal("invalid_queue_index", error.Code);
    }

    [Fact]
    public async Task A_queue_over_the_cap_is_trimmed_around_the_current_song()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        int[] queue = [.. Enumerable.Range(1, 3000)];

        var response = await kit.Accept(Alice, Report(Mac, claim: true, songId: 701, queue: queue, queueIndex: 700));

        var session = response.Session!;
        Assert.Equal(PlaybackRules.QueueCap, session.Queue.Count);
        Assert.Equal(50, session.QueueIndex);
        Assert.Equal(701, session.Queue[session.QueueIndex]);
    }

    [Fact]
    public async Task Display_hints_and_device_names_are_capped()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        var report = Report(Mac, claim: true, queue: [1]) with
        {
            Title = new string('t', 1000),
            DeviceName = "  " + new string('n', 100),
        };

        var session = (await kit.Accept(Alice, report)).Session!;

        Assert.Equal(512, session.Title!.Length);
        Assert.Equal(64, session.ActiveDeviceName!.Length);
    }

    // ── Versions ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_change_bumps_the_version()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        var mac = await kit.Connect(Alice, Mac);
        var versions = new List<long>();
        var seenByPhone = new List<long>();

        async Task Step(Func<Task<long>> change)
        {
            versions.Add(await change());
            seenByPhone.AddRange(Sessions(await Drain(phone)).Select(s => s.Version));
        }

        await Step(async () => (await kit.Claim(Alice, Mac)).Session!.Version);
        await Step(async () => (await kit.Accept(Alice, Report(Mac, isPlaying: false))).Session!.Version);
        // A heartbeat is a report too: it re-anchors the position every other device extrapolates.
        await Step(async () => (await kit.Accept(Alice, Report(Mac, isPlaying: false))).Session!.Version);
        // live → detached, although nobody reported anything.
        await Step(async () =>
        {
            mac.Dispose();
            kit.PastReconnectGrace();
            return (await kit.Snapshot(Alice)).Session!.Version;
        });
        // Coming back is not enough: a reconnect without a report changes nothing ...
        using var macAgain = await kit.Connect(Alice, Mac);
        Assert.Equal(4, (await kit.Snapshot(Alice)).Session!.Version);
        // ... until it reports: detached → live.
        await Step(async () => (await kit.Accept(Alice, Report(Mac, isPlaying: false))).Session!.Version);
        await Step(async () => (await kit.Claim(Alice, Phone)).Session!.Version);

        Assert.Equal([1, 2, 3, 4, 5, 6], versions);
        Assert.Equal(versions, seenByPhone);
    }

    [Fact]
    public async Task A_read_does_not_bump_the_version()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        var version = (await kit.Claim(Alice, Mac)).Session!.Version;

        kit.Advance(10);

        Assert.Equal(version, (await kit.Snapshot(Alice)).Session!.Version);
        Assert.Equal(version, (await kit.Snapshot(Alice)).Session!.Version);
    }

    // ── Position ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_position_is_extrapolated_as_of_the_message()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac, positionMs: 10_000);

        kit.Advance(5);
        Assert.Equal(15_000, (await kit.Snapshot(Alice)).Session!.PositionMs);

        await kit.Accept(Alice, Report(Mac, songId: 2, queueIndex: 1, positionMs: 15_000, isPlaying: false));
        kit.Advance(30);
        Assert.Equal(15_000, (await kit.Snapshot(Alice)).Session!.PositionMs);

        await kit.Accept(Alice, Report(Mac, songId: 2, queueIndex: 1, positionMs: 15_000, playbackRate: 2.0));
        kit.Advance(5);
        Assert.Equal(25_000, (await kit.Snapshot(Alice)).Session!.PositionMs);
    }

    [Fact]
    public async Task A_session_that_ran_far_past_its_end_is_reported_as_stopped()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac, positionMs: 50_000, durationMs: 60_000);
        await Drain(phone);

        kit.Advance(39);
        kit.Coordinator.Sweep();
        var nearEnd = (await kit.Snapshot(Alice)).Session!;
        Assert.True(nearEnd.IsPlaying);
        Assert.Equal(60_000, nearEnd.PositionMs);
        Assert.Empty(Sessions(await Drain(phone)));

        kit.Advance(2); // 10 s of song + 30 s of grace have passed with no report
        kit.Coordinator.Sweep();

        // The sweep says so without anyone asking, so remote devices stop their own clocks.
        var stopped = Assert.Single(Sessions(await Drain(phone)));
        Assert.False(stopped.IsPlaying);
        Assert.Equal(60_000, stopped.PositionMs);
        Assert.True(stopped.Live);

        // And it is the session now, not a projection: paused at the end, as of when it stopped.
        Assert.Equal(kit.Now.AddSeconds(-1), stopped.UpdatedAtUtc);
        var row = Assert.Single(kit.Coordinator.TakeDirty());
        Assert.False(row.IsPlaying);
        Assert.Equal(60_000, row.PositionMs);
    }

    // ── Reachability ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Closing_the_active_devices_last_stream_detaches_and_freezes_the_session()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        var macTab = await kit.Connect(Alice, Mac);
        var macSecondStream = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac, positionMs: 10_000);
        await Drain(phone);

        kit.Advance(5);
        macTab.Dispose();
        Assert.True((await kit.Snapshot(Alice)).Session!.Live); // streams are ref-counted

        macSecondStream.Dispose();
        Assert.Empty(await Drain(phone)); // for all anyone knows, it is reconnecting

        kit.PastReconnectGrace();
        var events = await Drain(phone);
        var detached = Assert.Single(Sessions(events));
        Assert.False(detached.Live);
        Assert.False(detached.IsPlaying);
        Assert.Equal(15_000, detached.PositionMs); // where its last stream closed

        // Still listed, as the active device, but offline.
        var mac = Assert.Single(DeviceLists(events).Last(), d => d.DeviceId == Mac);
        Assert.False(mac.Online);
        Assert.True(mac.IsActive);

        kit.Advance(60);
        Assert.Equal(15_000, (await kit.Snapshot(Alice)).Session!.PositionMs);
    }

    [Fact]
    public async Task The_reachability_window_running_out_detaches_the_session()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac, positionMs: 0, durationMs: 600_000);
        await Drain(phone);

        // The Mac froze: its stream is still open and even takes pings, but it stopped reporting.
        // Pings keep the phone listed; they do not keep the active device live.
        for (var t = 15; t <= 75; t += 15)
        {
            kit.Advance(15);
            mac.Delivered();
            phone.Delivered();
            kit.Coordinator.Sweep();
        }
        Assert.True((await kit.Snapshot(Alice)).Session!.Live);
        Assert.Empty(Sessions(await Drain(phone)));

        kit.Advance(1); // 76 s since the Mac's last report
        mac.Delivered();
        phone.Delivered();
        kit.Coordinator.Sweep();

        var detached = Assert.Single(Sessions(await Drain(phone)));
        Assert.False(detached.Live);
        Assert.False(detached.IsPlaying);
        Assert.Equal(75_000, detached.PositionMs); // frozen where the window ran out

        // Its connection still takes writes, so it stays listed like any idle device: a transfer
        // could still reach it. The session is what it no longer holds live.
        var devices = (await kit.Snapshot(Alice)).Devices;
        Assert.Contains(devices, d => d.DeviceId == Mac && d.IsActive && d.Online);
        Assert.Contains(devices, d => d.DeviceId == Phone && d.Online);
        Assert.Equal("device_offline", (await kit.Command(Alice, from: Phone, "pause")).Error!.Code);
    }

    [Fact]
    public async Task A_heartbeat_keeps_the_session_live()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);

        for (var i = 0; i < 12; i++)
        {
            kit.Advance(20);
            await kit.Accept(Alice, Report(Mac, songId: 2, queueIndex: 1, isPlaying: false));
            kit.Coordinator.Sweep();
        }

        Assert.True((await kit.Snapshot(Alice)).Session!.Live);
    }

    [Fact]
    public async Task A_device_whose_stream_comes_back_carries_on_from_its_last_report()
    {
        // A routine reconnect (the server ends every stream after a while) or a network blip: the
        // stream drops and comes back while the device keeps playing. Nobody is told anything.
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac, positionMs: 10_000);
        await Drain(phone);

        kit.Advance(2);
        mac.Dispose();
        kit.Advance(3);
        kit.Coordinator.Sweep();
        using var reconnected = await kit.Connect(Alice, Mac);
        kit.Coordinator.Sweep();

        var session = (await kit.Snapshot(Alice)).Session!;
        Assert.True(session.Live);
        Assert.True(session.IsPlaying);
        Assert.Equal(15_000, session.PositionMs);
        Assert.Empty(await Drain(phone)); // no session event, no devices event
        Assert.Empty(await Drain(reconnected));
    }

    [Fact]
    public async Task A_routine_reconnect_of_an_idle_device_is_not_announced()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        var phone = await kit.Connect(Alice, Phone);
        await Drain(mac);

        phone.Dispose();
        kit.Advance(1);
        kit.Coordinator.Sweep();
        using var phoneAgain = await kit.Connect(Alice, Phone);
        kit.PastReconnectGrace();

        Assert.Empty(await Drain(mac));
        Assert.Contains((await kit.Snapshot(Alice)).Devices, d => d.DeviceId == Phone && d.Online);
    }

    [Fact]
    public async Task A_device_that_comes_back_without_reporting_finds_the_session_where_it_left()
    {
        // The phone is playing, then the app is killed; a minute later it is opened again with
        // nothing loaded, so its stream connects but it has nothing to report.
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        var phone = await kit.Connect(Alice, Phone);
        await kit.Claim(Alice, Phone, positionMs: 60_000, durationMs: 240_000);

        kit.Advance(5);
        phone.Dispose();
        kit.PastReconnectGrace();
        var remembered = (await kit.Snapshot(Alice)).Session!;
        Assert.False(remembered.Live);
        Assert.False(remembered.IsPlaying);
        Assert.Equal(65_000, remembered.PositionMs);
        await Drain(mac);

        kit.Advance(49); // within the reachability window of its last report
        using var reopened = await kit.Connect(Alice, Phone);
        kit.Advance(80);
        kit.Coordinator.Sweep();

        // Not revived, and not played on through the time it was gone: nobody was told anything.
        var still = (await kit.Snapshot(Alice)).Session!;
        Assert.False(still.Live);
        Assert.False(still.IsPlaying);
        Assert.Equal(65_000, still.PositionMs);
        Assert.Equal(remembered.Version, still.Version);
        Assert.Empty(Sessions(await Drain(mac)));
        Assert.Equal(65_000, Assert.Single(kit.Coordinator.TakeDirty()).PositionMs);
        Assert.Equal(409, (await kit.Command(Alice, from: Mac, "resume")).Error!.StatusCode);

        // Once it reports, it holds the session again.
        var back = await kit.Accept(Alice, Report(Phone, songId: 2, queueIndex: 1, positionMs: 65_000, isPlaying: false));
        Assert.True(back.Accepted);
        Assert.True(back.Session!.Live);
    }

    [Fact]
    public async Task A_holder_that_comes_back_without_reporting_is_listed_and_can_be_handed_the_session()
    {
        // The same phone, opened again with nothing loaded: it does not revive the session, but it
        // is in the user's hand with its stream open, so the Mac must be able to pick it.
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        var phone = await kit.Connect(Alice, Phone);
        await kit.Claim(Alice, Phone, positionMs: 60_000, durationMs: 240_000);

        kit.Advance(5);
        phone.Dispose();
        kit.PastReconnectGrace();
        Assert.Contains((await kit.Snapshot(Alice)).Devices, d => d.DeviceId == Phone && d.IsActive && !d.Online);
        await Drain(mac);

        kit.Advance(60);
        using var reopened = await kit.Connect(Alice, Phone);
        Assert.Contains(DeviceLists(await Drain(mac)).Last(), d => d.DeviceId == Phone && d.IsActive && d.Online);
        for (var t = 0; t < 150; t += 15) // well past the reachability window of its last report
        {
            kit.Advance(15);
            mac.Delivered();
            reopened.Delivered();
            kit.Coordinator.Sweep();
        }

        var snapshot = await kit.Snapshot(Alice);
        Assert.False(snapshot.Session!.Live);
        Assert.False(snapshot.Session.IsPlaying);
        Assert.Equal(65_000, snapshot.Session.PositionMs);
        Assert.Contains(snapshot.Devices, d => d.DeviceId == Phone && d.IsActive && d.Online);

        // Remote control still needs it live; handing it the session does not.
        foreach (var remote in new[] { "pause", "resume", "next", "previous" })
            Assert.Equal("device_offline", (await kit.Command(Alice, from: Mac, remote)).Error!.Code);
        Assert.Equal("device_offline", (await kit.Command(Alice, from: Mac, "seek", positionMs: 1_000)).Error!.Code);

        var transfer = await kit.Command(Alice, from: Mac, "transfer", target: Phone);
        Assert.Null(transfer.Error);
        var command = Assert.Single(Commands(await Drain(reopened)));
        Assert.Equal("transfer", command.Command);
        Assert.False((await kit.Snapshot(Alice)).Session!.Live); // only its claim makes it live

        var claimed = await kit.Claim(Alice, Phone, positionMs: 65_000, inResponseTo: command.CommandId);
        Assert.True(claimed.Session!.Live);
        Assert.Equal(command.CommandId, claimed.Session.LastCommandId);
    }

    [Fact]
    public async Task A_command_sent_while_the_target_reconnects_reaches_its_next_stream()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);

        mac.Dispose();
        var sent = await kit.Command(Alice, from: Phone, "pause");
        Assert.Null(sent.Error);
        var late = await kit.Command(Alice, from: Phone, "next");
        kit.Advance(1);
        using var reconnected = await kit.Connect(Alice, Mac);

        // Written after the new stream's snapshot, in the order they were sent.
        Assert.Equal(
            [sent.Value!.CommandId, late.Value!.CommandId],
            Commands(await Drain(reconnected)).Select(c => c.CommandId));
    }

    [Fact]
    public async Task A_held_command_its_sender_has_given_up_on_is_dropped()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);

        mac.Dispose();
        Assert.Null((await kit.Command(Alice, from: Phone, "transfer", target: Mac)).Error);
        kit.Advance(PlaybackRules.CommandHold.TotalSeconds + 1);
        using var reconnected = await kit.Connect(Alice, Mac);

        Assert.Empty(Commands(await Drain(reconnected)));
    }

    [Fact]
    public async Task Pings_keep_an_idle_device_listed()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        using var tablet = await kit.Connect(Alice, Tablet);

        for (var i = 0; i < 12; i++)
        {
            kit.Advance(15);
            phone.Delivered(); // the tablet's pings stopped getting through
            kit.Coordinator.Sweep();
        }

        var devices = (await kit.Snapshot(Alice)).Devices;
        Assert.Contains(devices, d => d.DeviceId == Phone && d.Online && !d.IsActive);
        Assert.DoesNotContain(devices, d => d.DeviceId == Tablet);
    }

    [Fact]
    public async Task A_device_connecting_or_leaving_is_announced()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        Assert.Contains(mac.Snapshot.Devices, d => d.DeviceId == Mac);

        var phone = await kit.Connect(Alice, Phone);
        var joined = DeviceLists(await Drain(mac)).Last();
        Assert.Equal([Mac, Phone], joined.Select(d => d.DeviceId).Order());

        phone.Dispose();
        kit.PastReconnectGrace();
        var left = DeviceLists(await Drain(mac)).Last();
        Assert.Equal([Mac], left.Select(d => d.DeviceId));
    }

    [Fact]
    public async Task The_newcomers_snapshot_already_includes_everything()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);

        using var phone = await kit.Connect(Alice, Phone);

        Assert.Equal(Mac, phone.Snapshot.Session!.ActiveDeviceId);
        Assert.Equal(2, phone.Snapshot.Devices.Count);
        Assert.Empty(await Drain(phone)); // nothing it did not already know
    }

    // ── Tenancy ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_account_never_sees_or_commands_anothers_session()
    {
        // Same device id string on both accounts: an id only means something inside one account.
        var kit = new PlaybackTestKit();
        using var aliceMac = await kit.Connect(Alice, Mac);
        using var bobMac = await kit.Connect(Bob, Mac);
        await kit.Claim(Alice, Mac);

        var bobs = await kit.Snapshot(Bob);
        Assert.Null(bobs.Session);
        Assert.All(bobs.Devices, d => Assert.False(d.IsActive));
        Assert.DoesNotContain(await Drain(bobMac), e => e.Type == PlaybackEventTypes.Session);

        var bobsPause = await kit.Command(Bob, from: Mac, "pause");
        Assert.Equal("no_session", bobsPause.Error!.Code);
        Assert.Empty(Commands(await Drain(aliceMac)));

        // Bob playing something does not touch Alice's session either.
        await kit.Accept(Bob, Report(Mac, claim: true, songId: 77, queue: [77]));
        Assert.Equal(2, (await kit.Snapshot(Alice)).Session!.SongId);
        Assert.Empty(Sessions(await Drain(aliceMac)));
    }
}
