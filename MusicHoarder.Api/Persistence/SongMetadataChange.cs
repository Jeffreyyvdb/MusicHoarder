using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One field-level change the enrichment pipeline made (or proposed) to a song's metadata.
/// Gives the user a visible, reversible history so an automatic "improvement" can never
/// silently degrade a curated library. <see cref="AppliedAtUtc"/> is null for a *proposed*
/// change that was held back for review rather than applied automatically.
/// </summary>
public class SongMetadataChange
{
    [Key]
    public int Id { get; set; }

    public int SongId { get; set; }
    public SongMetadata Song { get; set; } = null!;

    [MaxLength(64)]
    public required string FieldName { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>Provider name or "consensus" that produced the change.</summary>
    [MaxLength(64)]
    public required string Source { get; set; }

    public double Confidence { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set when the change was actually applied to the row. Null = proposed/pending review.</summary>
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>Set when a previously-applied change was reverted by the user.</summary>
    public DateTime? RevertedAtUtc { get; set; }
}
