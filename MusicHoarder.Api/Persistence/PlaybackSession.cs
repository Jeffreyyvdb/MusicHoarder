using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// An account's remembered playback session — the queue, the current song, where it was and which
/// device last held it — so a cold device can offer "continue what you played this morning" after
/// API restarts, closed tabs and killed apps. One row per user.
///
/// <para>
/// This is the durable half only. The live half (which devices are connected, who is reachable,
/// pending commands) lives in <see cref="Playback.PlaybackCoordinator"/>, which loads this row
/// lazily, treats it as <b>paused</b> until its device reports again, and writes it back through
/// <see cref="Playback.PlaybackSessionFlushService"/> in batches rather than on every position
/// report. Only song ids and display hints are stored: never a stream URL, share token or path.
/// </para>
/// </summary>
public class PlaybackSession
{
    public int Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public int SongId { get; set; }

    /// <summary>The queue as a JSON array of song ids, already trimmed to the 1000-id cap.</summary>
    public string QueueJson { get; set; } = "[]";

    public int QueueIndex { get; set; }

    /// <summary>The position when the row was written (extrapolated to that moment while playing).</summary>
    public long PositionMs { get; set; }

    public long? DurationMs { get; set; }

    /// <summary>
    /// Informational: whether the session was playing when written. A loaded session is always
    /// treated as paused, because nothing proves the device is still playing after a restart.
    /// </summary>
    public bool IsPlaying { get; set; }

    public double PlaybackRate { get; set; } = 1.0;

    public int? RadioSeedId { get; set; }

    public bool Shuffle { get; set; }

    // Display hints, so a device that cannot resolve the song id still has something to show.
    [MaxLength(512)]
    public string? Title { get; set; }

    [MaxLength(512)]
    public string? Artist { get; set; }

    [MaxLength(512)]
    public string? Album { get; set; }

    [MaxLength(64)]
    public string? ActiveDeviceId { get; set; }

    [MaxLength(64)]
    public string? ActiveDeviceName { get; set; }

    /// <summary>Bumped on every accepted change; clients apply a session only when it is newer.</summary>
    public long Version { get; set; }

    /// <summary>When a device last reported an accepted change (the server's clock).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
