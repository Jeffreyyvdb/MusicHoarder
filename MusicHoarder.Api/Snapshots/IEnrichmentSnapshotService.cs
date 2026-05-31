using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Snapshots;

/// <summary>
/// Captures point-in-time, library-wide snapshots of enrichment-pipeline quality so changes to the
/// pipeline (new providers, tweaked thresholds, a different AI model, …) can be tracked on a timeline
/// and diffed for per-song regressions. See <see cref="EnrichmentSnapshot"/>.
/// </summary>
public interface IEnrichmentSnapshotService
{
    /// <summary>
    /// Capture a snapshot for <paramref name="ownerId"/>. Returns the created row, or <c>null</c> when
    /// the snapshot was skipped because nothing changed since the owner's latest snapshot (same config
    /// fingerprint and identical aggregate metrics).
    /// </summary>
    Task<EnrichmentSnapshot?> CaptureAsync(
        Guid ownerId, SnapshotTrigger trigger, string? triggerLabel, CancellationToken ct = default);
}
