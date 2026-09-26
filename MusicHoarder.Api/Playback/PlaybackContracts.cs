namespace MusicHoarder.Api.Playback;

// The playback-sync wire contract, shared by the web client and the Android client. Every
// property name here is a JSON field name both of them read (camelCase via the web serializer
// defaults), so a rename is a breaking change for two codebases, and for Android builds already in
// the field. Only song ids and display hints travel: never a stream URL, share token or path.

/// <summary>One player instance of the caller's account: a browser tab, or an Android install.</summary>
/// <param name="InstallId">One per browser profile (web) or install (Android), so another tab of the same browser can be labelled "Another tab".</param>
/// <param name="Kind"><c>computer</c>, <c>phone</c>, <c>tablet</c> or <c>unknown</c>: drives the icon only.</param>
/// <param name="Client"><c>web</c> or <c>android</c>.</param>
/// <param name="Online">There to be picked, so a <c>transfer</c> can reach it: an open stream (or one that closed seconds ago, as a routine reconnect does), and heard from — a report, a connect or a stream write — within the reachability window. For the active device this is not <see cref="PlaybackSessionDto.Live"/>: a holder that reconnected with nothing to report is online while its session stays remembered until it reports.</param>
/// <param name="IsActive">Holds the session. The active device is listed even while offline.</param>
public sealed record PlaybackDeviceDto(
    string DeviceId,
    string? InstallId,
    string Name,
    string Kind,
    string Client,
    bool Online,
    bool IsActive);

/// <summary>The account's playback session as every device sees it.</summary>
/// <param name="Version">Bumped on every change, live↔detached flips included. Apply a <c>session</c> event only when it is newer than the last one applied.</param>
/// <param name="PositionMs">As of the moment the server produced this message; extrapolated while playing and clamped to the duration.</param>
/// <param name="IsPlaying">False whenever the session is detached or its position ran well past the end without a report, whatever the device last said.</param>
/// <param name="Live">The active device is reachable. False means a detached (remembered) session.</param>
/// <param name="LastCommandId">The most recent command the active device acknowledged, so a sender can tell its command landed.</param>
public sealed record PlaybackSessionDto(
    long Version,
    int SongId,
    string? Title,
    string? Artist,
    string? Album,
    IReadOnlyList<int> Queue,
    int QueueIndex,
    long PositionMs,
    long? DurationMs,
    bool IsPlaying,
    double PlaybackRate,
    int? RadioSeedId,
    bool Shuffle,
    string? ActiveDeviceId,
    string? ActiveDeviceName,
    bool Live,
    string? LastCommandId,
    DateTime UpdatedAtUtc);

/// <summary><c>GET /api/playback</c>, and the <c>snapshot</c> event that opens every stream.</summary>
public sealed record PlaybackSnapshotDto(PlaybackSessionDto? Session, IReadOnlyList<PlaybackDeviceDto> Devices);

/// <summary>The <c>session</c> stream event.</summary>
public sealed record PlaybackSessionEvent(PlaybackSessionDto Session);

/// <summary>The <c>devices</c> stream event.</summary>
public sealed record PlaybackDevicesEvent(IReadOnlyList<PlaybackDeviceDto> Devices);

/// <summary>The <c>command</c> stream event, delivered only to the target device's own streams.</summary>
public sealed record PlaybackCommandEvent(
    string CommandId,
    string Command,
    long? PositionMs,
    string FromDeviceId,
    string? FromDeviceName);

/// <summary>The <c>ping</c> stream event: an empty object every 15 s that clients ignore.</summary>
public sealed record PlaybackPingEvent;

/// <summary>
/// <c>POST /api/playback/state</c>: a device's report of its own player. Fields are nullable where a
/// missing value has to be told apart from a default one (a null <see cref="Queue"/> means
/// "unchanged"; a null <see cref="SongId"/> is rejected). Positions and durations are taken as any
/// JSON number and rounded, so <c>audio.currentTime * 1000</c> needs no rounding on the way out.
/// </summary>
/// <param name="Claim">A local play intent just happened here, or this device executed a transfer/resume addressed to it. Always makes it the active device.</param>
/// <param name="InResponseTo">The command this report acknowledges; becomes the session's <c>lastCommandId</c> when the report is accepted.</param>
public sealed record PlaybackStateReport(
    string? DeviceId,
    string? InstallId,
    string? DeviceName,
    string? DeviceKind,
    string? Client,
    bool Claim,
    string? InResponseTo,
    int? SongId,
    string? Title,
    string? Artist,
    string? Album,
    int[]? Queue,
    int QueueIndex,
    double PositionMs,
    double? DurationMs,
    bool IsPlaying,
    double? PlaybackRate,
    int? RadioSeedId,
    bool Shuffle);

/// <summary><c>POST /api/playback/state</c>'s answer. <see cref="Accepted"/> false tells a device it no longer holds the session and must stop playing.</summary>
public sealed record PlaybackStateResponse(bool Accepted, PlaybackSessionDto? Session);

/// <summary><c>POST /api/playback/command</c>: remote control of the device holding the session, or a transfer to another one.</summary>
/// <param name="TargetDeviceId">Required for <c>transfer</c>; otherwise optional and, when given, must be the active device.</param>
/// <param name="Command"><c>pause</c>, <c>resume</c>, <c>next</c>, <c>previous</c>, <c>seek</c> or <c>transfer</c>.</param>
/// <param name="PositionMs">Required for <c>seek</c>.</param>
public sealed record PlaybackCommandRequest(
    string? FromDeviceId,
    string? TargetDeviceId,
    string? Command,
    double? PositionMs);

/// <summary><c>202</c> for an accepted command.</summary>
public sealed record PlaybackCommandAccepted(string CommandId);

/// <summary>The SSE <c>event:</c> names.</summary>
public static class PlaybackEventTypes
{
    public const string Snapshot = "snapshot";
    public const string Session = "session";
    public const string Devices = "devices";
    public const string Command = "command";
    public const string Ping = "ping";
}
