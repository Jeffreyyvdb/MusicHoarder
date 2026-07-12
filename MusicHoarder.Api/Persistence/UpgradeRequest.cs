namespace MusicHoarder.Api.Persistence;

public enum UpgradeRequestStatus
{
    Queued = 0,
    Searching = 1,
    Downloading = 2,

    /// <summary>Better file downloaded and identity-stamped; waiting for scan+fingerprint so the
    /// merge can verify it against the target and swap it in.</summary>
    AwaitingIngest = 3,

    Completed = 4,
    NotFound = 5,
    Failed = 6,
    Cancelled = 7,
}

/// <summary>What created the request: a user clicking "find better quality" vs the background
/// auto-upgrade sweep. Telemetry/UI only — the worker and merge treat both identically.</summary>
public enum UpgradeTrigger
{
    Manual = 0,
    Auto = 1,
}

/// <summary>
/// One manual "find a better copy of this track on Soulseek" request. The explicit
/// <see cref="SongId"/> ↔ <see cref="DownloadedFilePath"/> link is the whole point: a downloaded
/// re-encode never shares the target's Chromaprint string, so without this record the pipeline
/// couldn't know file X is intended to REPLACE song Y (rather than being a new track) until after
/// a full re-enrichment. The merge swaps the target's source file in place, preserving its Id.
/// </summary>
public class UpgradeRequest
{
    private const int MaxErrorLength = 1024;

    public int Id { get; set; }

    public int SongId { get; set; }
    public SongMetadata? Song { get; set; }

    public Guid OwnerUserId { get; set; }

    public UpgradeRequestStatus Status { get; set; } = UpgradeRequestStatus.Queued;

    /// <summary>Whether this request was queued manually or by the automatic upgrade sweep.</summary>
    public UpgradeTrigger Trigger { get; set; } = UpgradeTrigger.Manual;

    /// <summary>Normalized path of the downloaded candidate in the download staging dir.</summary>
    public string? DownloadedFilePath { get; set; }

    /// <summary>The candidate's advertised AudioQuality score at selection time (real file facts win at merge).</summary>
    public int? CandidateQualityScore { get; set; }

    /// <summary>JSON blob of the elected candidate (username/filename/bitrate/size) for the UI.</summary>
    public string? CandidateInfoJson { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public bool IsActive => Status is UpgradeRequestStatus.Queued
        or UpgradeRequestStatus.Searching
        or UpgradeRequestStatus.Downloading
        or UpgradeRequestStatus.AwaitingIngest;

    public void MarkSearching()
    {
        Status = UpgradeRequestStatus.Searching;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkDownloading(int candidateScore, string candidateInfoJson)
    {
        Status = UpgradeRequestStatus.Downloading;
        CandidateQualityScore = candidateScore;
        CandidateInfoJson = candidateInfoJson;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkAwaitingIngest(string downloadedFilePath)
    {
        Status = UpgradeRequestStatus.AwaitingIngest;
        DownloadedFilePath = downloadedFilePath;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkTerminal(UpgradeRequestStatus status, string? error = null)
    {
        Status = status;
        Error = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
        CompletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
