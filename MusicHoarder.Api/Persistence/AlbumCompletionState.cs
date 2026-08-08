using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>What <c>AlbumCompletionSweep</c> decided about an album the last time it looked.</summary>
public enum AlbumCompletionStatus
{
    /// <summary>Missing tracks were found and queued.</summary>
    Filled = 0,

    /// <summary>The album is ineligible (compilation, Various Artists, artist mismatch, too small).</summary>
    Skipped = 1,

    /// <summary>Eligible, but the owner already had every canonical track.</summary>
    NothingMissing = 2,
}

/// <summary>
/// One owner's album-completion verdict for one <see cref="CanonicalAlbum"/> — the sweep's "already
/// looked at this" marker and its backfill cursor in one row.
/// <para>
/// Owner-scoped even though <see cref="CanonicalAlbum"/> is shared catalog data, for the same reason
/// <see cref="CanonicalAlbumQualityGrade"/> is: the verdict depends on which tracks <em>this</em> owner
/// holds, so a column on the shared album would let one owner's completion suppress another's.
/// </para>
/// </summary>
public class AlbumCompletionState
{
    [Key]
    public int Id { get; set; }

    /// <summary>Owner whose library the verdict was computed against.</summary>
    public Guid OwnerUserId { get; set; }

    public int CanonicalAlbumId { get; set; }
    public CanonicalAlbum CanonicalAlbum { get; set; } = null!;

    public AlbumCompletionStatus Status { get; set; }

    public DateTime LastSweptAtUtc { get; set; }

    /// <summary>
    /// When the album becomes eligible for another look. Null means never again on a timer — used for
    /// <see cref="AlbumCompletionStatus.Skipped"/>, since a compilation does not stop being one. A
    /// re-fetched canonical album still forces a re-sweep regardless, via
    /// <see cref="CanonicalAlbum.FetchedAtUtc"/> being newer than <see cref="LastSweptAtUtc"/>.
    /// </summary>
    public DateTime? NextSweepAfterUtc { get; set; }

    /// <summary>Owned-track count at sweep time (snapshot — the library may change later).</summary>
    public int OwnedTrackCount { get; set; }

    /// <summary>Canonical (reconciled) track count at sweep time.</summary>
    public int CanonicalTrackCount { get; set; }

    /// <summary>How many wishlist items this sweep created for the album.</summary>
    public int EnqueuedTrackCount { get; set; }

    /// <summary>Why the album was skipped, e.g. <c>various-artists</c> or <c>artist-mismatch</c>. Diagnostic.</summary>
    [MaxLength(64)]
    public string? SkipReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
