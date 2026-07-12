namespace MusicHoarder.Api.Sync;

/// <summary>
/// Receive-side ingest for instance sync: answers "do you have this track?" probes and applies
/// uploaded tracks (create new rows, or replace an existing row's file in place preserving its Id).
/// </summary>
public interface ISyncIngestService
{
    Task<SyncCheckResponse> CheckAsync(SyncCheckRequest request, CancellationToken ct);

    /// <summary>
    /// Applies one uploaded track. <paramref name="file"/> is the raw audio stream (already
    /// size-capped by the endpoint); the payload carries the pushing instance's authoritative
    /// metadata. Idempotent: re-uploading an identical or worse file is a no-op skip.
    /// </summary>
    Task<SyncUploadResponse> IngestAsync(SyncTrackPayload payload, Stream file, CancellationToken ct);

    /// <summary>
    /// Applies a like-only change (no file) for a track already present here: matches by the portable
    /// identity ladder and sets its <see cref="Persistence.SongMetadata.LikedAtUtc"/>. Returns
    /// <c>Matched=false</c> when the track isn't present yet (the pusher will retry after the file syncs).
    /// </summary>
    Task<SyncLikeResponse> ApplyLikeAsync(SyncLikeRequest request, CancellationToken ct);
}
