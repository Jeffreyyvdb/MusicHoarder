namespace MusicHoarder.Api.Persistence;

public enum TrackSyncStatus
{
    Pending = 0,
    Uploading = 1,
    Synced = 2,

    /// <summary>The remote already holds this track at the same or better quality — nothing to send.</summary>
    SkippedRemoteBetter = 3,

    Failed = 4,
}

/// <summary>
/// Push-side sync outbox: one row per song tracking delivery to the remote instance. A separate
/// table (not fields on <see cref="SongMetadata"/>) so the sweep has a cheap dedicated index, the
/// hub entity stays lean, and at-least-once delivery survives restarts. Re-arming is
/// fingerprint-based: after a local quality upgrade the song's <see cref="SongMetadata.Fingerprint"/>
/// no longer equals <see cref="SyncedFingerprint"/>, so the sweep picks it up again and the remote's
/// replace-in-place path swaps its copy.
/// </summary>
public class TrackSyncState
{
    public int Id { get; set; }

    public int SongId { get; set; }
    public SongMetadata? Song { get; set; }

    public TrackSyncStatus Status { get; set; } = TrackSyncStatus.Pending;

    /// <summary>The fingerprint that was current when this song last synced/skipped.</summary>
    public string? SyncedFingerprint { get; set; }

    public int Attempts { get; set; }

    /// <summary>Earliest next retry for a Failed row (exponential backoff). Null = retry any time.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>Remote row id, informational only — ids are never used for cross-instance identity.</summary>
    public int? RemoteSongId { get; set; }

    public int? RemoteQualityScore { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public void MarkUploading()
    {
        Status = TrackSyncStatus.Uploading;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkSynced(string? fingerprint, int? remoteSongId, int? remoteQualityScore)
    {
        Status = TrackSyncStatus.Synced;
        SyncedFingerprint = fingerprint;
        RemoteSongId = remoteSongId;
        RemoteQualityScore = remoteQualityScore;
        LastError = null;
        NextAttemptAtUtc = null;
        Attempts = 0;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkSkippedRemoteBetter(string? fingerprint, int? remoteSongId, int? remoteQualityScore)
    {
        Status = TrackSyncStatus.SkippedRemoteBetter;
        SyncedFingerprint = fingerprint;
        RemoteSongId = remoteSongId;
        RemoteQualityScore = remoteQualityScore;
        LastError = null;
        NextAttemptAtUtc = null;
        Attempts = 0;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkFailed(string error, int retryBaseDelaySeconds, int maxAttempts)
    {
        Status = TrackSyncStatus.Failed;
        LastError = error.Length <= 1024 ? error : error[..1024];
        Attempts++;
        // Exponential backoff capped at an hour; past maxAttempts the row parks (no next attempt)
        // until a manual retry or a fingerprint change re-arms it.
        NextAttemptAtUtc = Attempts >= maxAttempts
            ? null
            : DateTime.UtcNow.AddSeconds(Math.Min(3600, retryBaseDelaySeconds * Math.Pow(2, Attempts)));
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>True when the sweep may hand this row to the worker again.</summary>
    public bool IsRetryable(int maxAttempts) =>
        Status == TrackSyncStatus.Failed
        && Attempts < maxAttempts
        && (NextAttemptAtUtc is null || NextAttemptAtUtc <= DateTime.UtcNow);
}
