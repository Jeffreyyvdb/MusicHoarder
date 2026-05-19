using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Settings;

/// <summary>
/// Read/write access to the small subset of <see cref="MusicEnricherOptions"/> that the Settings UI
/// can mutate at runtime. The DB row overlays the bound configuration; any null column falls back to
/// the configured value.
/// </summary>
public interface IRuntimeSettingsService
{
    Task<EffectiveSettings> GetAsync(CancellationToken ct = default);

    Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default);
}

public sealed record EffectiveSettings(
    bool EnableAcoustIdProvider,
    bool EnableMusicBrainzWebProvider,
    bool EnableSpotifyApiProvider,
    bool EnableTrackerProvider,
    double SpotifyApiMatchedThreshold,
    double AcoustIdScoreThreshold,
    int EnrichmentWorkerConcurrency,
    int LibraryBuilderWorkerConcurrency,
    DateTime? UpdatedAtUtc);

/// <summary>
/// All fields optional; null means "leave the existing override in place". Callers that need to
/// explicitly clear an override should pass the configured default (see <see cref="MusicEnricherOptions"/>).
/// </summary>
public sealed record RuntimeSettingsUpdate
{
    public bool? EnableAcoustIdProvider { get; init; }
    public bool? EnableMusicBrainzWebProvider { get; init; }
    public bool? EnableSpotifyApiProvider { get; init; }
    public bool? EnableTrackerProvider { get; init; }
    public double? SpotifyApiMatchedThreshold { get; init; }
    public double? AcoustIdScoreThreshold { get; init; }
    public int? EnrichmentWorkerConcurrency { get; init; }
    public int? LibraryBuilderWorkerConcurrency { get; init; }
}
