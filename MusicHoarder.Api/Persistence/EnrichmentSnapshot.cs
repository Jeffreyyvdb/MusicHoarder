using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>What caused a snapshot to be captured.</summary>
public enum SnapshotTrigger
{
    /// <summary>A scan/fingerprint/enrich/build run finalized (the auto-capture default).</summary>
    PipelineRun = 0,

    /// <summary>An AI quality-grading run finished (so fresh AI scores land on the timeline).</summary>
    GradingRun = 1,

    /// <summary>Explicit "capture snapshot" request (force a baseline).</summary>
    Manual = 2,
}

/// <summary>
/// A point-in-time, library-wide measurement of enrichment-pipeline quality, tied to the behavioral
/// "version" of the pipeline at capture time (<see cref="ConfigJson"/>/<see cref="ConfigHash"/>).
/// One row per timeline point; aggregate metrics live here, per-song state in
/// <see cref="EnrichmentSnapshotSong"/>. Rows are never updated — they form the version history.
/// </summary>
public class EnrichmentSnapshot
{
    [Key]
    public int Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public SnapshotTrigger Trigger { get; set; }

    /// <summary>The run's trigger label (e.g. enrichment cycle label), when known.</summary>
    [MaxLength(256)]
    public string? TriggerLabel { get; set; }

    /// <summary>Best-effort runtime version (assembly informational version / configured override / "dev").</summary>
    [MaxLength(128)]
    public string? Version { get; set; }

    /// <summary>
    /// Behavioral fingerprint of the pipeline at capture time: enabled providers, consensus/matching
    /// thresholds, AI model + prompt version. This is the real "what changed" signal during local
    /// iteration where the semver/git sha don't move between tweaks.
    /// </summary>
    public string ConfigJson { get; set; } = string.Empty;

    /// <summary>Stable hash of <see cref="ConfigJson"/> — used for de-dup and config-diff detection.</summary>
    [MaxLength(64)]
    public string ConfigHash { get; set; } = string.Empty;

    // --- Aggregate enrichment metrics ---

    public int TotalSongs { get; set; }
    public int MatchedCount { get; set; }
    public int NeedsReviewCount { get; set; }
    public int FailedCount { get; set; }
    public int PendingCount { get; set; }
    public int DuplicateCount { get; set; }
    public int BuildDoneCount { get; set; }

    /// <summary>Average <see cref="SongMetadata.MatchConfidence"/> over matched/needs-review songs that have one.</summary>
    public double? AvgMatchConfidence { get; set; }

    /// <summary>Per-provider matched-attempt counts, as a JSON object keyed by provider name.</summary>
    public string? ProviderMatchedJson { get; set; }

    // --- Aggregate AI-quality metrics (latest grade per song) ---

    public int GradedCount { get; set; }

    /// <summary>Average AI score (0–100) over graded songs, excluding Ungradeable.</summary>
    public double? AvgAiScore { get; set; }

    public int AiExcellent { get; set; }
    public int AiGood { get; set; }
    public int AiQuestionable { get; set; }
    public int AiWrong { get; set; }
    public int AiUngradeable { get; set; }

    public List<EnrichmentSnapshotSong> Songs { get; set; } = [];
}
