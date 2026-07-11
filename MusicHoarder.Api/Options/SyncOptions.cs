using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

public enum SyncMode
{
    Off = 0,

    /// <summary>This instance pushes finished tracks to a remote MusicHoarder (the home role).</summary>
    Push = 1,

    /// <summary>This instance accepts pushed tracks on <c>/api/sync/*</c> (the public-VPS role).</summary>
    Receive = 2,
}

/// <summary>
/// Instance-to-instance track sync. One MusicHoarder (private, e.g. a homelab) pushes each track
/// after its library build finishes to another (public) instance over plain HTTPS — no VPN. The
/// receive side is gated by a shared API key (<c>X-Sync-Key</c>); with <see cref="Mode"/> not
/// <c>Receive</c> the endpoints 404, so the surface is invisible everywhere it isn't enabled.
/// </summary>
public class SyncOptions
{
    public const string SectionName = "Sync";

    public SyncMode Mode { get; set; } = SyncMode.Off;

    /// <summary>
    /// Shared secret sent/checked as <c>X-Sync-Key</c>. The same value is configured on both
    /// instances. This is the only thing between the internet and the receive endpoints, so it must
    /// be long and random (e.g. <c>openssl rand -base64 48</c>) — enforced by
    /// <see cref="SyncOptionsValidator"/> whenever <see cref="Mode"/> is not <c>Off</c>.
    /// Always from env/secret.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    // ── Push side ────────────────────────────────────────────────────────────

    /// <summary>Public HTTPS origin of the receiving instance's API, e.g. <c>https://musichoarder.app</c>.</summary>
    public string RemoteBaseUrl { get; set; } = string.Empty;

    /// <summary>Attempts before a track's sync is parked as Failed (recoverable via sweep/manual retry).</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Base for the exponential retry backoff (base * 2^attempts, capped at ~1h).</summary>
    [Range(5, 3600)]
    public int RetryBaseDelaySeconds { get; set; } = 30;

    [Range(5, 120)]
    public int CheckTimeoutSeconds { get; set; } = 15;

    [Range(30, 3600)]
    public int UploadTimeoutSeconds { get; set; } = 300;

    /// <summary>Concurrent track uploads. Kept small — sync is a background trickle, not a bulk job.</summary>
    [Range(1, 8)]
    public int PushConcurrency { get; set; } = 2;

    /// <summary>How often the push sweep re-checks for unsynced / retryable tracks.</summary>
    [Range(60, 3600)]
    public int SweepIntervalSeconds { get; set; } = 300;

    // ── Receive side ─────────────────────────────────────────────────────────

    /// <summary>
    /// Managed directory received files are written into. Added to the scan roots (crash-recovery
    /// safety net); rows are created directly by the ingest, so a scan normally finds them
    /// already-known. In-flight uploads stream into an <c>.incoming/</c> subfolder the scanner skips.
    /// </summary>
    public string SyncedSourceDirectory { get; set; } = string.Empty;

    /// <summary>Hard cap for one uploaded file. FLAC albums tracks regularly exceed 100 MB.</summary>
    [Range(1_000_000, 2_000_000_000)]
    public long MaxUploadBytes { get; set; } = 300_000_000;

    /// <summary>Duration tolerance for the fuzzy (artist+title+duration) match rung.</summary>
    [Range(100, 30_000)]
    public int DurationToleranceMs { get; set; } = 3000;

    public bool IsPushConfigured => Mode == SyncMode.Push
        && !string.IsNullOrWhiteSpace(RemoteBaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey);

    public bool IsReceiveConfigured => Mode == SyncMode.Receive
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(SyncedSourceDirectory);
}
