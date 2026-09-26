using System.Collections.Concurrent;

namespace MusicHoarder.Api.Playback;

/// <summary>A playback request's result: the value, or the error to answer with.</summary>
public readonly record struct PlaybackOutcome<T>(T? Value, PlaybackError? Error) where T : class
{
    public static implicit operator PlaybackOutcome<T>(T value) => new(value, null);
    public static implicit operator PlaybackOutcome<T>(PlaybackError error) => new(null, error);
}

/// <summary>
/// The live half of playback sync ("Connect"): per account, the one playback session, the devices
/// that are connected, and the streams to push changes and commands down. A singleton, because the
/// API runs as a single replica; the durable half is <see cref="Persistence.PlaybackSession"/>.
///
/// <para>
/// <b>The invariant.</b> An account has one session and at most one active device. A report with
/// <c>claim</c> (a local play intent) always makes its device the active one and replaces the
/// session; a report without it is accepted only from the active device. Everyone else is told
/// <c>accepted: false</c>, which is how a device that slept through the <c>session</c> event
/// learns it was superseded. A <c>transfer</c> never moves the session by itself: only the
/// target's own claim does, so the old device keeps playing until the new one actually plays.
/// </para>
///
/// <para>
/// <b>Reachability</b> decides whether the session is live. A device is reachable while it is
/// connected — an open stream, or its last one closed no more than
/// <see cref="PlaybackRules.ReconnectGrace"/> ago, which is all a routine reconnect looks like —
/// and was heard from within <see cref="PlaybackRules.ReachableWindow"/>. For the active device
/// "heard from" means a state report made during its current connection: it heartbeats every 20 s,
/// so a device that froze or died drops out and the session detaches, and one that went away and
/// merely reconnects holds the session again only once it says what it is playing. Any other
/// device reports nothing, so for it a connect or a stream write that went through (the 15 s ping)
/// counts; that is what keeps an idle tab in the device picker. Every stream is ended after a few
/// minutes, which is what drops a client that vanished without closing its connection.
/// </para>
///
/// <para>
/// <b>Online</b> is not the same thing: it is whether a device is there to be picked, and it is the
/// connection rule every device gets, the active one included. So a holder that came back with
/// nothing to report is listed, and a <c>transfer</c> reaches it (its claim then makes the session
/// live), while remote control of the session — pause, resume, next, previous, seek — still needs
/// the session live.
/// </para>
///
/// <para>
/// When a session stops being believed as playing — its device became unreachable, or it ran far
/// past its end without a report — the position every device was shown at that moment is written
/// into the session, paused. Nothing later (a bare reconnect, a restart) can then extrapolate it
/// through the outage: the remembered position is where it stopped.
/// </para>
///
/// <para>
/// Everything about one account happens under that account's lock, and nothing about one account
/// ever reaches another: device ids are only meaningful inside one user's state. Every change that
/// other devices should see bumps the session's version and is pushed as a <c>session</c> event;
/// device list changes are pushed as <c>devices</c>. Because reachability expires with time,
/// <see cref="Sweep"/> re-evaluates every account periodically and emits the flips it finds.
/// </para>
/// </summary>
public sealed class PlaybackCoordinator
{
    private readonly ConcurrentDictionary<Guid, UserPlayback> _users = new();
    private readonly IPlaybackSessionStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<PlaybackCoordinator> _logger;

    public PlaybackCoordinator(IPlaybackSessionStore store, TimeProvider time, ILogger<PlaybackCoordinator> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────

    public async Task<PlaybackSnapshotDto> GetSnapshotAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        lock (user.Gate)
        {
            var now = Now();
            Evaluate(user, now, changed: false);
            return Snapshot(user, now);
        }
    }

    // ── Streams ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers an open stream for <paramref name="device"/>. The snapshot is taken under the
    /// same lock that registers the subscriber, so no change can fall between the two.
    /// </summary>
    internal async Task<PlaybackSubscription> SubscribeAsync(
        Guid userId, PlaybackDeviceIdentity device, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct);
        lock (user.Gate)
        {
            var now = Now();

            // Settle what already happened first: a device coming back after it went away finds
            // the session frozen where it left, not played on through the time it was gone.
            Evaluate(user, now, changed: false);

            var entry = Upsert(user, device);
            if (!Connected(entry, now))
                entry.ConnectedSinceUtc = now; // a new connection, not a routine reconnect
            entry.Streams++;
            entry.StreamsZeroSinceUtc = null;
            entry.LastHeardUtc = now;

            // Tell the devices already listening first (the newcomer's snapshot includes itself).
            Evaluate(user, now, changed: false);

            var subscriber = new PlaybackSubscriber(device.DeviceId);
            user.Subscribers.Add(subscriber);

            // Commands sent while it was between two streams; written after its snapshot.
            foreach (var (command, sentAtUtc) in entry.HeldCommands)
            {
                if (now - sentAtUtc <= PlaybackRules.CommandHold)
                    subscriber.Enqueue(command);
            }
            entry.HeldCommands.Clear();

            _logger.LogDebug(
                "Playback stream opened for user {UserId} device {DeviceId} ({Streams} open)",
                userId, device.DeviceId, entry.Streams);
            return new PlaybackSubscription(this, userId, subscriber, Snapshot(user, now));
        }
    }

    internal void Unsubscribe(Guid userId, PlaybackSubscriber subscriber)
    {
        if (!_users.TryGetValue(userId, out var user)) return;
        lock (user.Gate)
        {
            if (!user.Subscribers.Remove(subscriber)) return;
            subscriber.Close();

            var now = Now();
            if (user.Devices.TryGetValue(subscriber.DeviceId, out var entry) && --entry.Streams <= 0)
            {
                entry.Streams = 0;
                entry.StreamsZeroSinceUtc = now;
            }
            _logger.LogDebug(
                "Playback stream closed for user {UserId} device {DeviceId}", userId, subscriber.DeviceId);
            Evaluate(user, now, changed: false);
        }
    }

    /// <summary>An event reached this stream's connection: the device is still there.</summary>
    internal void Delivered(Guid userId, PlaybackSubscriber subscriber)
    {
        if (!_users.TryGetValue(userId, out var user)) return;
        lock (user.Gate)
        {
            if (!user.Subscribers.Contains(subscriber)) return;
            if (!user.Devices.TryGetValue(subscriber.DeviceId, out var entry)) return;

            var now = Now();
            entry.LastHeardUtc = now;
            Evaluate(user, now, changed: false);
        }
    }

    // ── Reports ────────────────────────────────────────────────────────────────────────────

    public async Task<PlaybackOutcome<PlaybackStateResponse>> ReportAsync(
        Guid userId, PlaybackStateReport report, CancellationToken ct)
    {
        var deviceError = PlaybackRules.TryParseDevice(
            report.DeviceId, report.InstallId, report.DeviceName, report.DeviceKind, report.Client, out var device);
        if (deviceError is not null) return deviceError;

        // A device that stops reports isPlaying=false with its last song instead; the session is
        // never deleted, so there is no such thing as a report without a song.
        if (report.SongId is not > 0) return PlaybackError.BadRequest("song_required");
        if (report.Claim && report.Queue is null) return PlaybackError.BadRequest("queue_required");
        if (report.Queue is { } sent && !InRange(report.QueueIndex, sent.Length))
            return PlaybackError.BadRequest("invalid_queue_index");

        var user = await LoadAsync(userId, ct);
        lock (user.Gate)
        {
            var now = Now();
            var isActive = user.Session?.ActiveDeviceId == device.DeviceId;
            var accepted = report.Claim || isActive;

            // "queue: null" means unchanged, so the index is checked against the queue it points into.
            if (accepted && report.Queue is null && !InRange(report.QueueIndex, user.Session!.Queue.Length))
                return PlaybackError.BadRequest("invalid_queue_index");

            // Heard from, whether or not the report is accepted.
            var entry = Upsert(user, device);
            entry.LastReportUtc = now;
            entry.LastHeardUtc = now;

            if (accepted)
            {
                var session = user.Session ??= new SessionState();
                var (queue, index) = report.Queue is { } q
                    ? PlaybackRules.TrimQueue(q, report.QueueIndex)
                    : (session.Queue, report.QueueIndex);

                var duration = PlaybackRules.NormalizeDuration(report.DurationMs);
                session.SongId = report.SongId.Value;
                session.Title = PlaybackRules.Cap(report.Title);
                session.Artist = PlaybackRules.Cap(report.Artist);
                session.Album = PlaybackRules.Cap(report.Album);
                session.Queue = queue;
                session.QueueIndex = index;
                var position = PlaybackRules.Milliseconds(report.PositionMs);
                session.PositionMs = duration is { } d ? Math.Min(position, d) : position;
                session.DurationMs = duration;
                session.IsPlaying = report.IsPlaying;
                session.PlaybackRate = PlaybackRules.NormalizeRate(report.PlaybackRate);
                session.RadioSeedId = report.RadioSeedId;
                session.Shuffle = report.Shuffle;
                session.ActiveDeviceId = device.DeviceId;
                session.ActiveDeviceName = device.Name;
                session.UpdatedAtUtc = now;

                // Only a report that lands acknowledges a command; a stale one says nothing about it.
                if (AcknowledgedCommand(report.InResponseTo) is { } ack)
                    session.LastCommandId = ack;

                if (report.Claim && !isActive)
                    _logger.LogDebug(
                        "Playback for user {UserId} claimed by device {DeviceId}", userId, device.DeviceId);
            }

            Evaluate(user, now, changed: accepted);
            return new PlaybackStateResponse(accepted, SessionDto(user, now));
        }
    }

    // ── Commands ───────────────────────────────────────────────────────────────────────────

    public async Task<PlaybackOutcome<PlaybackCommandAccepted>> SendCommandAsync(
        Guid userId, PlaybackCommandRequest request, CancellationToken ct)
    {
        if (!PlaybackRules.TryParseCommand(request.Command, out var command))
            return PlaybackError.BadRequest("invalid_command");
        if (!PlaybackRules.IsValidDeviceId(request.FromDeviceId))
            return PlaybackError.BadRequest("invalid_device_id");
        if (request.TargetDeviceId is not null && !PlaybackRules.IsValidDeviceId(request.TargetDeviceId))
            return PlaybackError.BadRequest("invalid_device_id");
        if (command == PlaybackCommandKind.Seek && request.PositionMs is null)
            return PlaybackError.BadRequest("position_required");
        if (command == PlaybackCommandKind.Transfer && request.TargetDeviceId is null)
            return PlaybackError.BadRequest("target_required");

        var user = await LoadAsync(userId, ct);
        lock (user.Gate)
        {
            var now = Now();
            Evaluate(user, now, changed: false);

            if (user.Session is not { } session) return PlaybackError.NoSession;

            string? target;
            if (command == PlaybackCommandKind.Transfer)
            {
                // The target adopts the session and claims it; activeDeviceId moves only then.
                target = request.TargetDeviceId;
            }
            else
            {
                target = session.ActiveDeviceId;
                if (request.TargetDeviceId is not null && request.TargetDeviceId != target)
                    return PlaybackError.NotActiveDevice;
            }

            // Remote control acts on the session where it plays, so it needs the session live. A
            // transfer only needs the target there to pick it up: its claim is what makes it live.
            if (target is null
                || !user.Devices.TryGetValue(target, out var targetDevice)
                || !(command == PlaybackCommandKind.Transfer
                    ? Online(targetDevice, now)
                    : Reachability(user, targetDevice, now).Reachable))
                return PlaybackError.DeviceOffline;

            var evt = new PlaybackCommandEvent(
                Guid.NewGuid().ToString(),
                command.WireName(),
                request.PositionMs is { } position ? PlaybackRules.Milliseconds(position) : null,
                request.FromDeviceId!,
                user.Devices.TryGetValue(request.FromDeviceId!, out var sender) ? sender.Name : null);

            // Only the target's own streams: a command is addressed, never broadcast. Between the
            // two streams of a routine reconnect it has none, so the command waits for the next.
            var message = new PlaybackStreamEvent(PlaybackEventTypes.Command, evt);
            var enqueued = false;
            foreach (var subscriber in user.Subscribers)
            {
                if (subscriber.DeviceId != target) continue;
                subscriber.Enqueue(message);
                enqueued = true;
            }
            if (!enqueued)
                targetDevice.HeldCommands.Add((message, now));

            _logger.LogDebug(
                "Playback command {Command} ({CommandId}) for user {UserId} sent to device {DeviceId}",
                evt.Command, evt.CommandId, userId, target);
            return new PlaybackCommandAccepted(evt.CommandId);
        }
    }

    // ── Background ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-evaluates every loaded account: reachability expires with time, so a device that fell
    /// out of the window, or a "playing" session that ran far past its end, has to be noticed
    /// without anyone asking. Also forgets devices nobody has heard from.
    /// </summary>
    public void Sweep()
    {
        foreach (var user in _users.Values)
        {
            if (!user.Loaded) continue;
            lock (user.Gate)
            {
                var now = Now();
                Prune(user, now);
                Evaluate(user, now, changed: false);
            }
        }
    }

    /// <summary>
    /// The sessions to write: every one changed since the last flush, plus — on the final flush at
    /// shutdown — every one still playing, so its saved position is as fresh as possible. The
    /// position written is the projected one, and a taken session counts as clean until it
    /// changes again or <see cref="MarkDirty"/> hands it back after a failed write.
    /// </summary>
    public IReadOnlyList<PlaybackSessionRecord> TakeDirty(bool includePlaying = false)
    {
        var records = new List<PlaybackSessionRecord>();
        foreach (var user in _users.Values)
        {
            if (!user.Loaded) continue;
            lock (user.Gate)
            {
                if (user.Session is not { } session) continue;

                var (playing, position) = Project(user, Now());
                if (!user.Dirty && !(includePlaying && playing)) continue;

                user.Dirty = false;
                records.Add(new PlaybackSessionRecord(
                    user.UserId,
                    session.SongId,
                    session.Queue,
                    session.QueueIndex,
                    position,
                    session.DurationMs,
                    playing,
                    session.PlaybackRate,
                    session.RadioSeedId,
                    session.Shuffle,
                    session.Title,
                    session.Artist,
                    session.Album,
                    session.ActiveDeviceId,
                    session.ActiveDeviceName,
                    session.Version,
                    session.UpdatedAtUtc));
            }
        }
        return records;
    }

    public void MarkDirty(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds)
        {
            if (!_users.TryGetValue(userId, out var user)) continue;
            lock (user.Gate) user.Dirty = true;
        }
    }

    // ── State ──────────────────────────────────────────────────────────────────────────────

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private async Task<UserPlayback> LoadAsync(Guid userId, CancellationToken ct)
    {
        var user = _users.GetOrAdd(userId, id => new UserPlayback(id));
        if (user.Loaded) return user;

        await user.LoadLock.WaitAsync(ct);
        try
        {
            if (!user.Loaded)
            {
                var record = await _store.LoadAsync(userId, ct);
                lock (user.Gate)
                {
                    // Nothing proves the device is still playing after a restart: the remembered
                    // session is paused until its device reports again. It keeps its active device,
                    // so a device that reconnects and reports simply carries on.
                    if (record is not null && user.Session is null)
                        user.Session = SessionState.FromRecord(record);
                    user.Loaded = true;
                }
            }
        }
        finally
        {
            user.LoadLock.Release();
        }
        return user;
    }

    private static DeviceEntry Upsert(UserPlayback user, PlaybackDeviceIdentity device)
    {
        if (!user.Devices.TryGetValue(device.DeviceId, out var entry))
        {
            entry = new DeviceEntry(device.DeviceId);
            user.Devices[device.DeviceId] = entry;
        }
        entry.InstallId = device.InstallId;
        entry.Name = device.Name;
        entry.Kind = device.Kind;
        entry.Client = device.Client;
        return entry;
    }

    private static void Prune(UserPlayback user, DateTime now)
    {
        List<string>? stale = null;
        foreach (var entry in user.Devices.Values)
        {
            if (entry.Streams == 0
                && entry.DeviceId != user.Session?.ActiveDeviceId
                && now - entry.LastHeardUtc > PlaybackRules.ReachableWindow)
                (stale ??= []).Add(entry.DeviceId);
        }
        if (stale is null) return;
        foreach (var id in stale) user.Devices.Remove(id);
    }

    /// <summary>
    /// Publishes whatever changed. <paramref name="changed"/> is an accepted report; a live↔detached
    /// flip, or the session starting or stopping being reported as playing, is a change too, even
    /// though no device said anything — each bumps the version so clients apply it.
    /// </summary>
    private void Evaluate(UserPlayback user, DateTime now, bool changed)
    {
        if (user.Session is { } session)
        {
            var live = IsLive(user, now);
            var projection = Project(user, now);
            if (session.IsPlaying && !projection.IsPlaying)
            {
                // Its device went away, or it ran far past the end without a word: where everyone
                // was shown it stopping becomes the session, paused.
                session.PositionMs = projection.PositionMs;
                session.UpdatedAtUtc = projection.FrozenAtUtc ?? now;
                session.IsPlaying = false;
                user.Dirty = true;
            }

            if (changed || live != user.EmittedLive || projection.IsPlaying != user.EmittedPlaying)
            {
                session.Version++;
                user.Dirty = true;
                user.EmittedLive = live;
                user.EmittedPlaying = projection.IsPlaying;
                Broadcast(user, new PlaybackStreamEvent(
                    PlaybackEventTypes.Session, new PlaybackSessionEvent(SessionDto(user, now)!)));
            }
        }

        var devices = DeviceList(user, now);
        if (!devices.SequenceEqual(user.EmittedDevices))
        {
            user.EmittedDevices = devices;
            Broadcast(user, new PlaybackStreamEvent(PlaybackEventTypes.Devices, new PlaybackDevicesEvent(devices)));
        }
    }

    private static void Broadcast(UserPlayback user, PlaybackStreamEvent evt)
    {
        foreach (var subscriber in user.Subscribers)
            subscriber.Enqueue(evt);
    }

    private static PlaybackSnapshotDto Snapshot(UserPlayback user, DateTime now) =>
        new(SessionDto(user, now), DeviceList(user, now));

    private static PlaybackSessionDto? SessionDto(UserPlayback user, DateTime now)
    {
        if (user.Session is not { } s) return null;

        var (playing, position) = Project(user, now);
        return new PlaybackSessionDto(
            s.Version,
            s.SongId,
            s.Title,
            s.Artist,
            s.Album,
            s.Queue,
            s.QueueIndex,
            position,
            s.DurationMs,
            playing,
            s.PlaybackRate,
            s.RadioSeedId,
            s.Shuffle,
            s.ActiveDeviceId,
            s.ActiveDeviceName,
            IsLive(user, now),
            s.LastCommandId,
            s.UpdatedAtUtc);
    }

    /// <summary>Online devices, plus the active one even while it is not.</summary>
    private static List<PlaybackDeviceDto> DeviceList(UserPlayback user, DateTime now)
    {
        var activeId = user.Session?.ActiveDeviceId;
        var devices = new List<PlaybackDeviceDto>();
        foreach (var entry in user.Devices.Values)
        {
            var isActive = entry.DeviceId == activeId;
            var online = Online(entry, now);
            if (online || isActive)
                devices.Add(new PlaybackDeviceDto(
                    entry.DeviceId, entry.InstallId, entry.Name, entry.Kind, entry.Client, online, isActive));
        }

        // A stable order, so an unchanged list never looks changed.
        devices.Sort((a, b) =>
        {
            var byActive = b.IsActive.CompareTo(a.IsActive);
            if (byActive != 0) return byActive;
            var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.DeviceId, b.DeviceId);
        });
        return devices;
    }

    private static bool IsLive(UserPlayback user, DateTime now) =>
        user.Session?.ActiveDeviceId is { } id
        && user.Devices.TryGetValue(id, out var entry)
        && Reachability(user, entry, now).Reachable;

    private static PlaybackProjection Project(UserPlayback user, DateTime now)
    {
        var s = user.Session!;
        DateTime? unreachableSince = null;
        if (s.IsPlaying)
        {
            if (s.ActiveDeviceId is { } id && user.Devices.TryGetValue(id, out var entry))
            {
                var (reachable, since) = Reachability(user, entry, now);
                if (!reachable) unreachableSince = since;
            }
            else
            {
                // Never seen since the API started: nothing moved since the last report.
                unreachableSince = s.UpdatedAtUtc;
            }
        }

        return PlaybackRules.Project(
            s.PositionMs, s.UpdatedAtUtc, s.DurationMs, s.IsPlaying, s.PlaybackRate, now, unreachableSince);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is reachable — for the active device, whether the session is
    /// live on it — and if not, since when: the moment its last stream closed or its window ran
    /// out, whichever came first. Not what the device list shows; that is <see cref="Online"/>.
    /// </summary>
    private static (bool Reachable, DateTime? UnreachableSince) Reachability(
        UserPlayback user, DeviceEntry entry, DateTime now)
    {
        var isActive = entry.DeviceId == user.Session?.ActiveDeviceId;
        var heard = isActive ? entry.LastReportUtc : entry.LastHeardUtc;
        var expiresAt = heard + PlaybackRules.ReachableWindow;

        // A report from before the active device's current connection says nothing about it.
        var reportedSinceConnecting =
            !isActive || entry.ConnectedSinceUtc is not { } connectedSince || heard >= connectedSince;

        var connected = Connected(entry, now);
        if (connected && reportedSinceConnecting && now <= expiresAt) return (true, null);

        var since = now > expiresAt ? expiresAt : now;
        if (!reportedSinceConnecting && entry.ConnectedSinceUtc < since) since = entry.ConnectedSinceUtc.Value;
        if (!connected)
        {
            var closedAt = entry.StreamsZeroSinceUtc ?? heard;
            if (closedAt < since) since = closedAt;
        }
        return (false, since);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is there to be picked: connected and heard from — a report,
    /// a connect or a stream write — within the window. The same rule for every device, the active
    /// one included, so a holder that came back with nothing to report is listed and can be
    /// handed the session although the session is not live on it (see <see cref="Reachability"/>).
    /// </summary>
    private static bool Online(DeviceEntry entry, DateTime now) =>
        Connected(entry, now) && now <= entry.LastHeardUtc + PlaybackRules.ReachableWindow;

    /// <summary>An open stream, or its last one closed no more than the reconnect grace ago.</summary>
    private static bool Connected(DeviceEntry entry, DateTime now) =>
        entry.Streams > 0
        || entry.StreamsZeroSinceUtc is { } closedAt && now <= closedAt + PlaybackRules.ReconnectGrace;

    private static bool InRange(int index, int length) => index >= 0 && index < length;

    private static string? AcknowledgedCommand(string? inResponseTo)
    {
        var id = inResponseTo?.Trim();
        return string.IsNullOrEmpty(id) || id.Length > PlaybackRules.MaxCommandIdLength ? null : id;
    }

    private sealed class UserPlayback(Guid userId)
    {
        public Guid UserId { get; } = userId;
        public object Gate { get; } = new();
        public SemaphoreSlim LoadLock { get; } = new(1, 1);

        private volatile bool _loaded;
        public bool Loaded
        {
            get => _loaded;
            set => _loaded = value;
        }

        public SessionState? Session { get; set; }
        public Dictionary<string, DeviceEntry> Devices { get; } = new(StringComparer.Ordinal);
        public List<PlaybackSubscriber> Subscribers { get; } = [];

        /// <summary>Changed since the last flush.</summary>
        public bool Dirty { get; set; }

        // What the streams were last told, so a flip nobody reported is still noticed.
        public bool EmittedLive { get; set; }
        public bool EmittedPlaying { get; set; }
        public List<PlaybackDeviceDto> EmittedDevices { get; set; } = [];
    }

    private sealed class DeviceEntry(string deviceId)
    {
        public string DeviceId { get; } = deviceId;
        public string? InstallId { get; set; }
        public string Name { get; set; } = PlaybackRules.UnnamedDevice;
        public string Kind { get; set; } = "unknown";
        public string Client { get; set; } = "web";

        /// <summary>Open streams; several with the same device id are counted, not replaced.</summary>
        public int Streams { get; set; }

        /// <summary>When its last stream closed; null while one is open.</summary>
        public DateTime? StreamsZeroSinceUtc { get; set; }

        /// <summary>When its current connection began; a reconnect within the grace continues it.</summary>
        public DateTime? ConnectedSinceUtc { get; set; }

        /// <summary>A state report. What keeps the active device live; a stream (re)connect does not.</summary>
        public DateTime LastReportUtc { get; set; }

        /// <summary>Anything: a report, a connect, or a stream write that went through.</summary>
        public DateTime LastHeardUtc { get; set; }

        /// <summary>Commands sent while it was between two streams, for the next one.</summary>
        public List<(PlaybackStreamEvent Command, DateTime SentAtUtc)> HeldCommands { get; } = [];
    }

    /// <summary>
    /// The session as the active device last reported it. <see cref="PositionMs"/> is anchored at
    /// <see cref="UpdatedAtUtc"/>; what other devices see is projected from the two.
    /// </summary>
    private sealed class SessionState
    {
        public int SongId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }

        /// <summary>Replaced, never mutated in place, so a DTO can share the array.</summary>
        public int[] Queue { get; set; } = [];

        public int QueueIndex { get; set; }
        public long PositionMs { get; set; }
        public long? DurationMs { get; set; }
        public bool IsPlaying { get; set; }
        public double PlaybackRate { get; set; } = 1.0;
        public int? RadioSeedId { get; set; }
        public bool Shuffle { get; set; }
        public string? ActiveDeviceId { get; set; }
        public string? ActiveDeviceName { get; set; }
        public string? LastCommandId { get; set; }
        public long Version { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public static SessionState FromRecord(PlaybackSessionRecord record) => new()
        {
            SongId = record.SongId,
            Title = record.Title,
            Artist = record.Artist,
            Album = record.Album,
            Queue = [.. record.Queue],
            QueueIndex = record.QueueIndex,
            PositionMs = record.PositionMs,
            DurationMs = record.DurationMs,
            IsPlaying = false,
            PlaybackRate = record.PlaybackRate,
            RadioSeedId = record.RadioSeedId,
            Shuffle = record.Shuffle,
            ActiveDeviceId = record.ActiveDeviceId,
            ActiveDeviceName = record.ActiveDeviceName,
            Version = record.Version,
            UpdatedAtUtc = record.UpdatedAtUtc,
        };
    }
}

/// <summary>One open stream's registration: its snapshot, its mailbox, and its unregistration.</summary>
internal sealed class PlaybackSubscription : IDisposable
{
    private readonly PlaybackCoordinator _coordinator;
    private readonly Guid _userId;
    private readonly PlaybackSubscriber _subscriber;
    private int _disposed;

    internal PlaybackSubscription(
        PlaybackCoordinator coordinator, Guid userId, PlaybackSubscriber subscriber, PlaybackSnapshotDto snapshot)
    {
        _coordinator = coordinator;
        _userId = userId;
        _subscriber = subscriber;
        Snapshot = snapshot;
    }

    public PlaybackSnapshotDto Snapshot { get; }

    /// <inheritdoc cref="PlaybackSubscriber.NextAsync"/>
    public Task<PlaybackStreamEvent?> NextAsync(TimeSpan pingAfter, CancellationToken ct) =>
        _subscriber.NextAsync(pingAfter, ct);

    /// <summary>The last event reached the connection.</summary>
    public void Delivered() => _coordinator.Delivered(_userId, _subscriber);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _coordinator.Unsubscribe(_userId, _subscriber);
    }
}
