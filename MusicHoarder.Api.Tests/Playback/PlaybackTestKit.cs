using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Playback;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Playback;

/// <summary>A clock the test moves by hand, so reachability and extrapolation are exact.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A session store that keeps rows in a dictionary and counts its writes.</summary>
internal sealed class InMemoryPlaybackSessionStore : IPlaybackSessionStore
{
    public Dictionary<Guid, PlaybackSessionRecord> Rows { get; } = [];
    public int Saves { get; private set; }
    public bool FailNextSave { get; set; }

    public Task<PlaybackSessionRecord?> LoadAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult(Rows.GetValueOrDefault(userId));

    public Task SaveAsync(IReadOnlyCollection<PlaybackSessionRecord> sessions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // as EF does on its first query
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new InvalidOperationException("database unavailable");
        }
        Saves++;
        foreach (var session in sessions) Rows[session.OwnerUserId] = session;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A coordinator on a manual clock, with helpers for the moves every test makes: connect a
/// device's stream, report, and read what a stream was sent.
/// </summary>
internal sealed class PlaybackTestKit
{
    public static readonly DateTimeOffset Start = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    public static Guid Alice => TestUsers.OwnerId;
    public static Guid Bob => TestUsers.FriendId;

    public const string Mac = "mac-0000-device";
    public const string Phone = "phone-000-device";
    public const string Tablet = "tablet-00-device";

    public PlaybackTestKit(IPlaybackSessionStore? store = null)
    {
        Store = store ?? new InMemoryPlaybackSessionStore();
        Coordinator = new PlaybackCoordinator(Store, Clock, NullLogger<PlaybackCoordinator>.Instance);
    }

    public ManualTimeProvider Clock { get; } = new(Start);
    public IPlaybackSessionStore Store { get; }
    public PlaybackCoordinator Coordinator { get; }

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public void Advance(double seconds) => Clock.Advance(TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Lets the reconnect grace of a stream that just closed run out, and sweeps, as the flusher
    /// does every few seconds: the device is now gone, not merely reconnecting.
    /// </summary>
    public void PastReconnectGrace()
    {
        Clock.Advance(PlaybackRules.ReconnectGrace + TimeSpan.FromSeconds(1));
        Coordinator.Sweep();
    }

    public static PlaybackDeviceIdentity Device(string deviceId, string client = "web") =>
        new(deviceId, null, NameOf(deviceId), deviceId == Mac ? "computer" : "phone", client);

    public Task<PlaybackSubscription> Connect(Guid user, string deviceId) =>
        Coordinator.SubscribeAsync(user, Device(deviceId), CancellationToken.None);

    public static PlaybackStateReport Report(
        string deviceId,
        bool claim = false,
        int? songId = 1,
        int[]? queue = null,
        int queueIndex = 0,
        long positionMs = 0,
        long? durationMs = 200_000,
        bool isPlaying = true,
        double? playbackRate = 1.0,
        string? inResponseTo = null,
        string? client = "web") =>
        new(
            deviceId,
            InstallId: null,
            NameOf(deviceId),
            deviceId == Mac ? "computer" : "phone",
            client,
            claim,
            inResponseTo,
            songId,
            Title: $"Song {songId}",
            Artist: "R.E.M.",
            Album: "Automatic for the People",
            queue,
            queueIndex,
            positionMs,
            durationMs,
            isPlaying,
            playbackRate,
            RadioSeedId: null,
            Shuffle: false);

    /// <summary>A local play intent on <paramref name="deviceId"/>: claims the session with a three-song queue.</summary>
    public Task<PlaybackStateResponse> Claim(
        Guid user, string deviceId, long positionMs = 0, bool isPlaying = true, long? durationMs = 200_000,
        string? inResponseTo = null) =>
        Accept(user, Report(
            deviceId, claim: true, songId: 2, queue: [1, 2, 3], queueIndex: 1,
            positionMs: positionMs, durationMs: durationMs, isPlaying: isPlaying, inResponseTo: inResponseTo));

    public async Task<PlaybackStateResponse> Accept(Guid user, PlaybackStateReport report)
    {
        var outcome = await Coordinator.ReportAsync(user, report, CancellationToken.None);
        Assert.Null(outcome.Error);
        return outcome.Value!;
    }

    public async Task<PlaybackError> Reject(Guid user, PlaybackStateReport report)
    {
        var outcome = await Coordinator.ReportAsync(user, report, CancellationToken.None);
        Assert.Null(outcome.Value);
        return outcome.Error!;
    }

    public Task<PlaybackOutcome<PlaybackCommandAccepted>> Command(
        Guid user, string from, string command, string? target = null, long? positionMs = null) =>
        Coordinator.SendCommandAsync(
            user, new PlaybackCommandRequest(from, target, command, positionMs), CancellationToken.None);

    public async Task<PlaybackSnapshotDto> Snapshot(Guid user) =>
        await Coordinator.GetSnapshotAsync(user, CancellationToken.None);

    /// <summary>Everything the stream has been sent and not yet read, oldest first.</summary>
    public static async Task<List<PlaybackStreamEvent>> Drain(PlaybackSubscription subscription)
    {
        var events = new List<PlaybackStreamEvent>();
        while (await subscription.NextAsync(TimeSpan.Zero, CancellationToken.None) is { } evt
               && !ReferenceEquals(evt, PlaybackStreamEvent.Ping))
            events.Add(evt);
        return events;
    }

    public static IEnumerable<PlaybackSessionDto> Sessions(IEnumerable<PlaybackStreamEvent> events) =>
        events.Where(e => e.Type == PlaybackEventTypes.Session)
            .Select(e => ((PlaybackSessionEvent)e.Payload).Session);

    public static IEnumerable<IReadOnlyList<PlaybackDeviceDto>> DeviceLists(IEnumerable<PlaybackStreamEvent> events) =>
        events.Where(e => e.Type == PlaybackEventTypes.Devices)
            .Select(e => ((PlaybackDevicesEvent)e.Payload).Devices);

    public static IEnumerable<PlaybackCommandEvent> Commands(IEnumerable<PlaybackStreamEvent> events) =>
        events.Where(e => e.Type == PlaybackEventTypes.Command)
            .Select(e => (PlaybackCommandEvent)e.Payload);

    private static string NameOf(string deviceId) => deviceId switch
    {
        Mac => "Safari on Mac",
        Phone => "Safari on iPhone",
        Tablet => "Chrome on Android",
        _ => deviceId,
    };
}
