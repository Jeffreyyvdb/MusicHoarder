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

    /// <summary>
    /// Overlays <see cref="Options.MusicEnricherOptions.EnableAlbumCompletion"/> — when true the sweep
    /// queues the missing tracks of albums the owner already holds part of. In the DB rather than only
    /// in config so the owner can opt in (and back out) without a redeploy; the config flag still gates
    /// it, so an instance that never wants the feature can leave it off entirely.
    /// </summary>
    public bool? AlbumCompletionEnabled { get; set; }

    /// <summary>
    /// Overlays <see cref="Options.MusicEnricherOptions.ReleaseStagedSourcesAfterBuild"/> — when true
    /// the hourly sweep deletes a download's staged copy once its library copy has been verified, so
    /// downloads stop being stored twice. In the DB so the owner can opt in (and back out) from the
    /// Settings UI without a redeploy; the hard gate is still a configured download directory.
    /// </summary>
    public bool? ReleaseStagedSourcesEnabled { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}
