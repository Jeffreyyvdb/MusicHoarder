using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Per-song state captured inside an <see cref="EnrichmentSnapshot"/>. This is the substrate for
/// version-to-version regression diffing: comparing two snapshots' rows for the same
/// <see cref="SongId"/> surfaces exactly which songs got better or worse.
/// </summary>
public class EnrichmentSnapshotSong
{
    [Key]
    public int Id { get; set; }

    public int SnapshotId { get; set; }
    public EnrichmentSnapshot Snapshot { get; set; } = null!;

    public int SongId { get; set; }

    public EnrichmentStatus EnrichmentStatus { get; set; }
    public double? MatchConfidence { get; set; }

    [MaxLength(64)]
    public string? MatchedBy { get; set; }

    public bool IsDuplicate { get; set; }

    /// <summary>Latest AI score (0–100) at capture time, when the song had been graded.</summary>
    public int? AiScore { get; set; }

    /// <summary>Latest AI verdict at capture time, when the song had been graded.</summary>
    public SongQualityVerdict? AiVerdict { get; set; }
}
