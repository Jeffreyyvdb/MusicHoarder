using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Singleton row holding the subset of <see cref="Options.MusicEnricherOptions"/> values that can
/// be tweaked at runtime from the Settings UI. Any field left null falls back to the bound
/// configuration value, so user-secrets / appsettings.json continue to act as defaults.
/// </summary>
public class RuntimeSettings
{
    [Key]
    public int Id { get; set; }

    public bool? EnableAcoustIdProvider { get; set; }
    public bool? EnableMusicBrainzWebProvider { get; set; }
    public bool? EnableSpotifyApiProvider { get; set; }
    public bool? EnableTrackerProvider { get; set; }

    public double? SpotifyApiMatchedThreshold { get; set; }
    public double? AcoustIdScoreThreshold { get; set; }

    public int? EnrichmentWorkerConcurrency { get; set; }
    public int? LibraryBuilderWorkerConcurrency { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}
