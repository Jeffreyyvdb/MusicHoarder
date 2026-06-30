using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Singleton row holding the subset of <see cref="Options.MusicEnricherOptions"/> (and
/// <see cref="Options.QualityGradingOptions"/>) values that can be tweaked at runtime from the
/// Settings UI. Any field left null falls back to the bound configuration value, so user-secrets /
/// appsettings.json continue to act as defaults.
/// </summary>
public class RuntimeSettings
{
    [Key]
    public int Id { get; set; }

    public bool? EnableAcoustIdProvider { get; set; }
    public bool? EnableMusicBrainzWebProvider { get; set; }
    public bool? EnableSpotifyApiProvider { get; set; }
    public bool? EnableTrackerProvider { get; set; }
    public bool? EnableDeezerProvider { get; set; }
    public bool? EnableAppleMusicProvider { get; set; }

    /// <summary>Overlays <see cref="Options.QualityGradingOptions.Enabled"/> — the AI quality grader master switch.</summary>
    public bool? QualityGradingEnabled { get; set; }

    /// <summary>
    /// Overlays <see cref="Options.MusicEnricherOptions.AutoDownloadWishlist"/> — when true the download
    /// worker auto-sweeps Pending wishlist items in the background instead of waiting for the explicit
    /// <c>POST /api/wishlist/download</c> trigger. Lets the owner flip auto-download from the Wishlist UI.
    /// </summary>
    public bool? AutoDownloadWishlist { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}
