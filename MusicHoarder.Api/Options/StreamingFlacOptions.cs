using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Connection settings for an optional, self-hosted "streaming FLAC" acquisition sidecar (the
/// <c>spotiflac</c> download provider). MusicHoarder never talks to any streaming service itself — it
/// only calls the sidecar's small HTTP contract (<c>/health</c>, <c>/acquire</c>) behind an
/// <see cref="IsConfigured"/> gate, exactly like the slskd integration.
/// <para>
/// The sidecar wraps a legally-grey third-party module and is deliberately kept out of the shipped
/// MusicHoarder image; nothing here imports or references it. With <see cref="SidecarUrl"/> empty the
/// integration is simply off: the provider reports "not found" so the wishlist chain falls straight
/// through to slskd / yt-dlp, and instances that never set it are byte-for-byte unaffected.
/// </para>
/// </summary>
public class StreamingFlacOptions
{
    public const string SectionName = "StreamingFlac";

    /// <summary>Master switch. Off unless a <see cref="SidecarUrl"/> is present (see <see cref="IsConfigured"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL of the acquisition sidecar, e.g. <c>http://spotiflac:8000</c>. Empty disables the
    /// integration. Always from env/secret — never committed.
    /// </summary>
    public string SidecarUrl { get; set; } = string.Empty;

    /// <summary>
    /// Ordered list of lossless services the sidecar should try, highest priority first. Restricted
    /// to services that return ready lossless audio (Tidal / Qobuz; Deezer optionally). Passed through
    /// verbatim to the sidecar.
    /// </summary>
    public string[] Services { get; set; } = ["qobuz", "tidal"];

    /// <summary>Requested quality tier. <c>LOSSLESS</c> keeps a HI_RES→LOSSLESS fallback (still lossless).</summary>
    public string Quality { get; set; } = "LOSSLESS";

    /// <summary>Per-track ceiling handed to the sidecar and used as the HTTP client's request timeout.</summary>
    [Range(10, 600)]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>True when a sidecar URL is present and the feature is enabled.</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(SidecarUrl);
}
