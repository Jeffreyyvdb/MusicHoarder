namespace MusicHoarder.Api.Persistence;

public enum IngestRunStatus
{
    Running,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// A persisted record of one ingest session — the window between the pipeline going from idle to
/// active (a scan/fingerprint/enrich/build kicking off) and settling back to idle. Owner-scoped
/// like <see cref="SongMetadata"/>; written by the background <c>IngestRunMonitor</c>, which sets
/// <see cref="OwnerUserId"/> explicitly because hosted-service scopes have no current user.
/// </summary>
public class IngestRun
{
    public Guid Id { get; set; }

    /// <summary>Owner of this run. Scoped by the EF global query filter.</summary>
    public Guid OwnerUserId { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public IngestRunStatus Status { get; set; }

    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of what triggered this run (e.g. "Manual enrich — Kanye West"),
    /// or null for background/auto runs. Set by <c>IngestRunMonitor</c> from the active enrichment
    /// cycle's label.
    /// </summary>
    public string? TriggerLabel { get; set; }

    public int TracksDiscovered { get; set; }
    public int TracksProcessed { get; set; }
    public int TracksFingerprinted { get; set; }
    public int TracksEnriched { get; set; }

    /// <summary>Tracks written to the destination library ("written" in the UI).</summary>
    public int TracksCopied { get; set; }

    public int TracksReview { get; set; }
    public int TracksFailed { get; set; }

    /// <summary>Files-per-second over the run's duration, computed at finalization.</summary>
    public double ThroughputPerSec { get; set; }

    /// <summary>
    /// Capped JSON array of the most recent activity rows captured for this run, surfaced as the
    /// detail panel's "tail of log". Null until first written.
    /// </summary>
    public string? LogTailJson { get; set; }
}
