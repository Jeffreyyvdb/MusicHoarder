using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Connection settings for a user-operated slskd instance (headless Soulseek client,
/// https://github.com/slskd/slskd). MusicHoarder never joins the Soulseek network itself — it only
/// talks to slskd's REST API to search and download. Like the other integrations it degrades
/// gracefully: with no <see cref="BaseUrl"/>/<see cref="ApiKey"/> the feature is simply off, the
/// download provider reports "not found" so the wishlist chain falls through to the next provider,
/// and upgrade endpoints return 400.
/// <para>
/// slskd itself (account, shares, ports) is entirely the user's responsibility; this app only needs
/// the API endpoint plus read access to slskd's completed-downloads directory (mount the same host
/// path into both containers).
/// </para>
/// </summary>
public class SlskdOptions
{
    public const string SectionName = "Slskd";

    /// <summary>Master switch. Off unless connection settings are present (see <see cref="IsConfigured"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>slskd web/API base URL, e.g. <c>http://slskd:5030</c>. Empty disables the integration.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key sent as <c>X-API-Key</c> (slskd <c>web.authentication.api_keys</c>). Always from env/secret.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// slskd's completed-downloads directory as seen from THIS process (bind the same host path that
    /// slskd writes to, read-write so finished files can be moved out). Files are moved into
    /// <c>MusicEnricher:DownloadDirectory</c> on completion, so this stays a transient staging area
    /// and is never indexed by the scanner.
    /// </summary>
    public string DownloadsDirectory { get; set; } = string.Empty;

    /// <summary>How long a Soulseek search runs before we collect whatever responses arrived.</summary>
    [Range(5, 120)]
    public int SearchTimeoutSeconds { get; set; } = 30;

    /// <summary>Delay between polls of an in-flight search.</summary>
    [Range(200, 10_000)]
    public int SearchPollIntervalMs { get; set; } = 1000;

    /// <summary>Per-candidate ceiling for a download to finish before we cancel and try the next peer.</summary>
    [Range(30, 3600)]
    public int DownloadTimeoutSeconds { get; set; } = 600;

    /// <summary>Delay between polls of an in-flight transfer.</summary>
    [Range(1, 30)]
    public int TransferPollIntervalSeconds { get; set; } = 2;

    /// <summary>File extensions considered acceptable in search results (leading dot, lowercase).</summary>
    public string[] AllowedExtensions { get; set; } = [".flac", ".mp3", ".m4a", ".ogg", ".opus"];

    /// <summary>Minimum advertised bitrate (kbps) for lossy candidates. Lossless candidates are exempt.</summary>
    [Range(0, 2000)]
    public int MinBitrateLossy { get; set; } = 200;

    /// <summary>Rank lossless candidates above lossy regardless of advertised bitrate.</summary>
    public bool PreferLossless { get; set; } = true;

    /// <summary>Max deviation between a candidate's advertised duration and the requested track's.</summary>
    [Range(1, 60)]
    public int DurationToleranceSeconds { get; set; } = 10;

    /// <summary>How many ranked candidates to attempt (next-best when a peer stalls or errors) per track.</summary>
    [Range(1, 10)]
    public int MaxCandidateAttempts { get; set; } = 3;

    /// <summary>
    /// Search rate limit. Soulseek etiquette: aggressive automated searching is a strong
    /// ban/ignore signal for peers and the server, so bulk runs are deliberately smoothed.
    /// </summary>
    [Range(1, 60)]
    public int SearchesPerMinute { get; set; } = 10;

    /// <summary>True when connection settings are present and the feature is enabled.</summary>
    public bool IsConfigured => Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(DownloadsDirectory);
}
