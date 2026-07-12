using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Two-way sync of song likes/favorites with a Navidrome server over its Subsonic API
/// (<c>star</c> / <c>unstar</c> / <c>getStarred2</c>). Entirely inert unless configured: with no
/// <see cref="BaseUrl"/> / <see cref="Username"/> / <see cref="Password"/> the background reconciler
/// never runs and the like endpoints still work locally (MH-only likes). Songs are matched to
/// Navidrome tracks by library-relative path first (MH's destination/source dirs map onto Navidrome
/// libraries), then MusicBrainz recording id, then a fuzzy artist+title+duration fallback.
/// </summary>
public class NavidromeOptions
{
    public const string SectionName = "Navidrome";

    /// <summary>The Subsonic client name sent as <c>c=</c> on every request.</summary>
    public const string ClientName = "musichoarder";

    /// <summary>Master switch. Even when true the sync stays off until credentials are present.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Navidrome origin, e.g. <c>http://navidrome:4533</c> or <c>https://navidrome.example.com</c>. Always from env/secret.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>Navidrome password. Sent only as a salted MD5 token (never in the clear). Always from env/secret.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>How often the full two-way reconcile sweep runs.</summary>
    [Range(30, 3600)]
    public int ReconcileIntervalSeconds { get; set; } = 120;

    /// <summary>Per-request HTTP timeout for Subsonic calls.</summary>
    [Range(5, 120)]
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>Max songs a <c>search3</c> resolution asks for when finding a track's Navidrome id.</summary>
    [Range(1, 100)]
    public int SearchLimit { get; set; } = 25;

    /// <summary>Duration tolerance for the fuzzy (artist+title+duration) match rung.</summary>
    [Range(0, 30)]
    public int FuzzyDurationToleranceSeconds { get; set; } = 8;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);
}
