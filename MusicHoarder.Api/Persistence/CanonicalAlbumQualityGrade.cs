using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One AI quality grade for a <see cref="CanonicalAlbum"/>'s reconciliation — i.e. whether the album
/// we linked to provider albums actually corresponds to the user's local album. Owner-scoped because
/// the judgment uses the owner's owned songs as ground truth. History-friendly like
/// <see cref="SongQualityGrade"/>: a re-grade inserts a new row; latest by <see cref="GradedAtUtc"/>
/// is current. Reuses <see cref="SongQualityVerdict"/> (the verdict scale is generic) so the same
/// rollups and frontend badges apply.
/// </summary>
public class CanonicalAlbumQualityGrade
{
    [Key]
    public int Id { get; set; }

    public int CanonicalAlbumId { get; set; }
    public CanonicalAlbum CanonicalAlbum { get; set; } = null!;

    /// <summary>Owner whose library was used as ground truth for the judgment.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>0–100 quality score the grader assigned to the reconciliation.</summary>
    public int Score { get; set; }

    public SongQualityVerdict Verdict { get; set; }

    [MaxLength(1024)]
    public string? Summary { get; set; }

    /// <summary>JSON array of structured issues, e.g. <c>[{"code":"wrong_edition","severity":"high"}]</c>.</summary>
    public string? IssuesJson { get; set; }

    [MaxLength(128)]
    public string? Model { get; set; }

    public int PromptVersion { get; set; }

    /// <summary>Stable hash of the dossier; a re-grade is skipped when fingerprint+model+prompt all match.</summary>
    [MaxLength(64)]
    public string? InputFingerprint { get; set; }

    /// <summary>Owned-track count at grade time (snapshot — the library may change later).</summary>
    public int OwnedTrackCount { get; set; }

    /// <summary>Canonical (reconciled) track count at grade time.</summary>
    public int CanonicalTrackCount { get; set; }

    public int? DurationMs { get; set; }

    public string? RawResponseJson { get; set; }

    public DateTime GradedAtUtc { get; set; }
}
