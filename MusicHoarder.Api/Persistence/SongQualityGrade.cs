using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Overall quality verdict an LLM assigned to a single song's enrichment result.
/// Ordered worst→best is intentional (numeric value rises with quality) so rollups
/// can <c>Min()</c> to find the weakest track in a directory.
/// </summary>
public enum SongQualityVerdict
{
    /// <summary>The grader could not evaluate (no data, parse failure). Excluded from quality averages.</summary>
    Ungradeable = 0,

    /// <summary>The applied/proposed metadata is wrong — different song, artist, or album than the source file.</summary>
    Wrong = 1,

    /// <summary>Plausible but unverified or internally inconsistent; a human should look.</summary>
    Questionable = 2,

    /// <summary>Correct and consistent, with minor gaps (missing year, loose album, etc.).</summary>
    Good = 3,

    /// <summary>Correct, corroborated, and complete.</summary>
    Excellent = 4,
}

/// <summary>
/// One AI quality grade for a song's enrichment outcome. History-friendly: a re-grade inserts a
/// new row, so we can benchmark the algorithm across runs / prompt versions / models. The latest
/// row (by <see cref="GradedAtUtc"/>) is the current grade. Never overwritten in place.
/// </summary>
public class SongQualityGrade
{
    [Key]
    public int Id { get; set; }

    public int SongId { get; set; }
    public SongMetadata Song { get; set; } = null!;

    /// <summary>Owner of the graded song. Mirrors <see cref="SongMetadata.OwnerUserId"/> for the tenancy filter.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>0–100 quality score the grader assigned to the enrichment result.</summary>
    public int Score { get; set; }

    public SongQualityVerdict Verdict { get; set; }

    /// <summary>One-sentence human-readable justification from the grader.</summary>
    [MaxLength(1024)]
    public string? Summary { get; set; }

    /// <summary>
    /// JSON array of structured issues the grader flagged, e.g.
    /// <c>[{"code":"artist_changed","severity":"high","detail":"..."}]</c>. Drives the
    /// directory/library rollups' "top issues" without re-parsing prose.
    /// </summary>
    public string? IssuesJson { get; set; }

    /// <summary>Model id that produced the grade (e.g. <c>openai/gpt-4o-mini</c>). Lets us compare models.</summary>
    [MaxLength(128)]
    public string? Model { get; set; }

    /// <summary>Prompt template version, so grades from different prompt iterations are comparable.</summary>
    public int PromptVersion { get; set; }

    /// <summary>
    /// Stable hash of the dossier the grade was computed from. A re-grade is skipped when the
    /// fingerprint, model, and prompt version all match the latest grade — avoids paying tokens
    /// to re-grade an unchanged song.
    /// </summary>
    [MaxLength(64)]
    public string? InputFingerprint { get; set; }

    /// <summary>The song's enrichment status at grade time (snapshot — the song may change later).</summary>
    [MaxLength(32)]
    public string? EnrichmentStatusAtGrade { get; set; }

    /// <summary>The destination path the song would have been written to at grade time ("WILL WRITE TO").</summary>
    public string? DestinationPathPreview { get; set; }

    /// <summary>Wall-clock the grading LLM call took, for cost/latency tracking.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Raw LLM response JSON, retained for the export / debugging the grader itself.</summary>
    public string? RawResponseJson { get; set; }

    public DateTime GradedAtUtc { get; set; }
}
