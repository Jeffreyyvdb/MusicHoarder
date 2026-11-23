namespace MusicHoarder.Api.Persistence;

public enum AlbumCoverFetchStatus
{
    /// <summary>Every enabled provider answered cleanly but none had a cover.</summary>
    NotFound = 0,

    /// <summary>At least one provider errored or was rate limited — worth retrying sooner.</summary>
    Failed = 1,

    /// <summary>
    /// Folder already has a Cover Art Archive cover on disk, and Spotify answered cleanly without a
    /// verified match to replace it. Filtered out of "no cover art" UI feeds.
    /// </summary>
    UpgradeNotFound = 2,

    /// <summary>
    /// Folder already has a Cover Art Archive cover on disk, and Spotify errored or rate-limited
    /// during the upgrade attempt — worth retrying sooner. Filtered out of "no cover art" UI feeds.
    /// </summary>
    UpgradeFailed = 3,
}

/// <summary>
/// Retry-cooldown state for the external cover art sweep, one row per destination album folder that
/// the external fetch could <b>not</b> produce a cover for. Success leaves no row — the on-disk
/// <c>cover.*</c> file is the success marker, and the sweep deletes the row when a later attempt (or
/// the user dropping a file in) resolves the folder. Catalog-style: no per-user query filter.
/// </summary>
public class AlbumCoverFetchAttempt
{
    public int Id { get; set; }

    /// <summary>Destination album directory (the cover pass key). Unique.</summary>
    public string AlbumFolder { get; set; } = string.Empty;

    public AlbumCoverFetchStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTime LastAttemptAtUtc { get; set; }

    /// <summary>Earliest next retry; null = never retry (NotFound with retries disabled).</summary>
    public DateTime? NextRetryAfterUtc { get; set; }
}
