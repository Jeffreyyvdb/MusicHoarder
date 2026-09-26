using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Playback;

/// <summary>
/// The pure rules of playback sync: the queue cap, the stale-playing rule, position extrapolation
/// and the limits on what a device may send. No state and no clock of their own, so each one is
/// tested case by case, and the web and Android clients port the queue trim from here.
/// </summary>
public static partial class PlaybackRules
{
    /// <summary>
    /// How recently a device must have been heard from to count as reachable (with an open stream).
    /// The active device heartbeats every 20 s, so this is three missed heartbeats plus slack.
    /// </summary>
    public static readonly TimeSpan ReachableWindow = TimeSpan.FromSeconds(75);

    /// <summary>
    /// How long a device whose streams all closed still counts as connected. Every stream is ended
    /// after a few minutes and its client reconnects within a second or so; this grace is what keeps
    /// that routine reconnect from flickering the device list or the session's <c>live</c> flag.
    /// </summary>
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a command for a device that is between two streams waits for the next one. Less
    /// than the 5 s a sender waits for it to land, so a late command is dropped rather than run
    /// after the sender has already offered to play it somewhere else.
    /// </summary>
    public static readonly TimeSpan CommandHold = TimeSpan.FromSeconds(3);

    /// <summary>How far past the end a "playing" session may run without a report before it is reported as stopped.</summary>
    public static readonly TimeSpan StaleAfterEnd = TimeSpan.FromSeconds(30);

    public const int QueueCap = 1000;

    /// <summary>How many already-played ids survive a trim, before the current song.</summary>
    public const int QueueHistory = 50;

    public const int MaxDisplayLength = 512;
    public const int MaxDeviceNameLength = 64;

    /// <summary>A command id is a GUID; anything longer is not one of ours and is ignored.</summary>
    public const int MaxCommandIdLength = 64;

    public const string UnnamedDevice = "Unknown device";

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex DeviceIdPattern();

    public static bool IsValidDeviceId(string? value) => value is not null && DeviceIdPattern().IsMatch(value);

    /// <summary>
    /// Caps a queue at <see cref="QueueCap"/> ids by the same rule the clients apply before
    /// sending: keep the current song, start <see cref="QueueHistory"/> ids before it, take up to
    /// the cap from there, and re-base the index. A queue already under the cap is returned as is,
    /// so a client that trimmed first never sees its queue move under it.
    /// </summary>
    public static (int[] Queue, int Index) TrimQueue(int[] queue, int index)
    {
        if (queue.Length <= QueueCap)
            return (queue, index);

        var start = Math.Max(0, index - QueueHistory);
        var count = Math.Min(QueueCap, queue.Length - start);
        return (queue[start..(start + count)], index - start);
    }

    /// <summary>
    /// Where a session is at <paramref name="now"/>, and whether it should still be reported as
    /// playing. While playing, the position advances from the last report at
    /// <paramref name="playbackRate"/> and is clamped to the duration. It stops advancing — frozen
    /// at the last extrapolated value — once the device became unreachable
    /// (<paramref name="unreachableSinceUtc"/>) or the position ran <see cref="StaleAfterEnd"/>
    /// past the end without a new report: in both cases the device has probably died, so the
    /// session is reported as paused rather than playing on forever.
    /// </summary>
    /// <param name="unreachableSinceUtc">Null while the active device is reachable.</param>
    public static PlaybackProjection Project(
        long positionMs,
        DateTime reportedAtUtc,
        long? durationMs,
        bool isPlaying,
        double playbackRate,
        DateTime now,
        DateTime? unreachableSinceUtc)
    {
        if (!isPlaying)
            return new(false, Clamp(positionMs, durationMs));

        var playing = true;
        var until = now;

        if (unreachableSinceUtc is { } since)
        {
            playing = false;
            if (since < until) until = since;
        }

        if (durationMs is { } duration)
        {
            var remainingMs = duration + StaleAfterEnd.TotalMilliseconds - positionMs;
            var overrunAt = reportedAtUtc + TimeSpan.FromMilliseconds(Math.Max(0, remainingMs / playbackRate));
            if (now >= overrunAt)
            {
                playing = false;
                if (overrunAt < until) until = overrunAt;
            }
        }

        var elapsedMs = Math.Max(0, (until - reportedAtUtc).TotalMilliseconds);
        var position = Clamp(positionMs + (long)(elapsedMs * playbackRate), durationMs);
        return playing
            ? new(true, position)
            : new(false, position) { FrozenAtUtc = until > reportedAtUtc ? until : reportedAtUtc };
    }

    private static long Clamp(long positionMs, long? durationMs)
    {
        var position = Math.Max(0, positionMs);
        return durationMs is { } duration ? Math.Min(position, duration) : position;
    }

    /// <summary>Anything that is not a finite, positive rate is taken as normal speed.</summary>
    public static double NormalizeRate(double? rate) =>
        rate is { } r && double.IsFinite(r) && r > 0 ? Math.Min(r, 16) : 1.0;

    /// <summary>A position in whole milliseconds, never negative.</summary>
    public static long Milliseconds(double value) =>
        double.IsFinite(value) && value > 0 ? (long)Math.Min(Math.Round(value), long.MaxValue / 2) : 0;

    /// <summary>A zero or negative duration means "unknown" (a stream that has not loaded its metadata yet).</summary>
    public static long? NormalizeDuration(double? durationMs) =>
        durationMs is { } d && Milliseconds(d) is > 0 and var ms ? ms : null;

    /// <summary>Trimmed and cut to <paramref name="max"/>; blank becomes null.</summary>
    public static string? Cap(string? value, int max = MaxDisplayLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max].TrimEnd();
    }

    public static string DeviceName(string? name) => Cap(name, MaxDeviceNameLength) ?? UnnamedDevice;

    /// <summary>The icon is the only thing this drives, so an unrecognised kind is simply <c>unknown</c>.</summary>
    public static string DeviceKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "computer" => "computer",
        "phone" => "phone",
        "tablet" => "tablet",
        _ => "unknown",
    };

    public static bool TryParseClient(string? client, out string normalized)
    {
        normalized = client?.Trim().ToLowerInvariant() switch
        {
            "web" => "web",
            "android" => "android",
            _ => string.Empty,
        };
        return normalized.Length > 0;
    }

    public static bool TryParseCommand(string? command, out PlaybackCommandKind kind)
    {
        kind = default;
        switch (command?.Trim().ToLowerInvariant())
        {
            case "pause": kind = PlaybackCommandKind.Pause; return true;
            case "resume": kind = PlaybackCommandKind.Resume; return true;
            case "next": kind = PlaybackCommandKind.Next; return true;
            case "previous": kind = PlaybackCommandKind.Previous; return true;
            case "seek": kind = PlaybackCommandKind.Seek; return true;
            case "transfer": kind = PlaybackCommandKind.Transfer; return true;
            default: return false;
        }
    }

    public static string WireName(this PlaybackCommandKind kind) => kind switch
    {
        PlaybackCommandKind.Pause => "pause",
        PlaybackCommandKind.Resume => "resume",
        PlaybackCommandKind.Next => "next",
        PlaybackCommandKind.Previous => "previous",
        PlaybackCommandKind.Seek => "seek",
        PlaybackCommandKind.Transfer => "transfer",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Validates and normalises a device's self-description (from the stream's query string or a
    /// state report). The ids are the only strict part: they key the device, so a malformed one is
    /// a 400. The name and kind are display hints and are made presentable instead.
    /// </summary>
    public static PlaybackError? TryParseDevice(
        string? deviceId,
        string? installId,
        string? name,
        string? kind,
        string? client,
        out PlaybackDeviceIdentity identity)
    {
        identity = null!;
        if (!IsValidDeviceId(deviceId))
            return PlaybackError.BadRequest("invalid_device_id");
        if (!string.IsNullOrEmpty(installId) && !IsValidDeviceId(installId))
            return PlaybackError.BadRequest("invalid_install_id");
        if (!TryParseClient(client, out var normalizedClient))
            return PlaybackError.BadRequest("invalid_client");

        identity = new PlaybackDeviceIdentity(
            deviceId!,
            string.IsNullOrEmpty(installId) ? null : installId,
            DeviceName(name),
            DeviceKind(kind),
            normalizedClient);
        return null;
    }
}

/// <summary>Where a session is at a given moment, and whether it is still to be reported as playing.</summary>
public readonly record struct PlaybackProjection(bool IsPlaying, long PositionMs)
{
    /// <summary>
    /// When a session its device reported as playing stopped advancing — the device went away, or
    /// the position ran far past the end — never before that report. Null while it still plays, and
    /// for a session that was paused anyway.
    /// </summary>
    public DateTime? FrozenAtUtc { get; init; }
}

public enum PlaybackCommandKind
{
    Pause,
    Resume,
    Next,
    Previous,
    Seek,
    Transfer,
}

/// <summary>A device's validated self-description.</summary>
public sealed record PlaybackDeviceIdentity(
    string DeviceId,
    string? InstallId,
    string Name,
    string Kind,
    string Client);

/// <summary>A rejected playback request: the HTTP status and the <c>error</c> code clients branch on.</summary>
public sealed record PlaybackError(int StatusCode, string Code)
{
    public static PlaybackError BadRequest(string code) => new(StatusCodes.Status400BadRequest, code);
    public static PlaybackError Conflict(string code) => new(StatusCodes.Status409Conflict, code);
    public static PlaybackError NotFound(string code) => new(StatusCodes.Status404NotFound, code);

    public static readonly PlaybackError NoSession = NotFound("no_session");
    public static readonly PlaybackError NotActiveDevice = Conflict("not_active_device");
    public static readonly PlaybackError DeviceOffline = Conflict("device_offline");
}
