using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// What a clean-edit check found about a built file. The numbers are a persisted contract: append, never renumber.
/// </summary>
public enum CleanEditVerdict
{
    /// <summary>The file follows the explicit edition where the explicit and clean editions differ.</summary>
    Explicit = 0,

    /// <summary>The file follows the label's clean edition where the two editions differ.</summary>
    CleanMaster = 1,

    /// <summary>Words the explicit edition sings are silent in the file (a mute that is not the label's clean edit).</summary>
    MutedWords = 2,

    /// <summary>An explicit edition was found, but nothing in the preview window settles which one the file is.</summary>
    Inconclusive = 3,

    /// <summary>Only an explicit edition is sold, so there is nothing clean to confuse the file with.</summary>
    NoCleanEdition = 4,

    /// <summary>No explicit edition could be found and aligned, so there is nothing to compare with.</summary>
    NoReference = 5,

    /// <summary>A lookup, download or decode failed. The next run checks the song again.</summary>
    Failed = 6,
}

/// <summary>
/// The one current clean-edit check of a song: re-checking replaces the row. It is derived from the
/// built file and free previews, and never changes the file or the song row.
/// </summary>
public class CleanEditCheck
{
    [Key]
    public int Id { get; set; }

    public int SongId { get; set; }
    public SongMetadata Song { get; set; } = null!;

    /// <summary>Owner of the checked song. Mirrors <see cref="SongMetadata.OwnerUserId"/> for the tenancy filter.</summary>
    public Guid OwnerUserId { get; set; }

    public CleanEditVerdict Verdict { get; set; }

    /// <summary>How decisively the closer edition won, 0 to 1 (pairwise test only).</summary>
    public double? Margin { get; set; }

    /// <summary>Seconds of audio where the two editions differ (pairwise test only).</summary>
    public double? EditedSeconds { get; set; }

    /// <summary>The references used and where words were muted, as <c>CleanEditEvidence</c> JSON.</summary>
    public string? EvidenceJson { get; set; }

    /// <summary>The file this was checked against (path and size), so a replaced file is checked again.</summary>
    [MaxLength(2048)]
    public string FileStamp { get; set; } = "";

    public DateTime CheckedAtUtc { get; set; }
}
