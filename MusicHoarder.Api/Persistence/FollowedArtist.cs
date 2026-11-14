using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The kinds of release a catalog lists for an artist. Flags so a followed artist can name the kinds
/// it watches; the numbers are stored, so append only.
/// </summary>
[Flags]
public enum ArtistReleaseKinds
{
    None = 0,
    Album = 1,
    Ep = 2,
    Single = 4,
    Compilation = 8,
}

/// <summary>
/// An artist the owner follows: a catalog artist (Deezer, which needs no key) whose new releases the
/// <c>ArtistMonitorSweep</c> looks for, and — when <see cref="AutoDownload"/> is on — queues as
/// <see cref="WishlistItemOrigin.ArtistMonitor"/> wishlist items. Owner-scoped.
/// <para>
/// Follows a catalog id, never a name: two artists can share a name, and the person picked this one.
/// </para>
/// </summary>
public class FollowedArtist
{
    public int Id { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>The Deezer artist id. One follow per (owner, artist).</summary>
    [MaxLength(64)]
    public string DeezerArtistId { get; set; } = string.Empty;

    /// <summary>The catalog's spelling of the name, as the follow found it.</summary>
    [MaxLength(512)]
    public string Name { get; set; } = string.Empty;

    /// <summary><c>TitleNormalizer.NormalizeForSearch(Name)</c>: how the artist page finds its follow.</summary>
    [MaxLength(512)]
    public string ArtistKey { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? PictureUrl { get; set; }

    /// <summary>Which kinds of new release count. Albums and EPs by default: singles flood quickly.</summary>
    public ArtistReleaseKinds ReleaseKinds { get; set; } = ArtistReleaseKinds.Album | ArtistReleaseKinds.Ep;

    /// <summary>Queue new releases for download. Off means the monitor only notices and notifies.</summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>
    /// The baseline: everything the catalog listed when the follow was made is recorded as
    /// <see cref="FollowedReleaseDecision.Baseline"/> and never downloaded by the monitor ("Get
    /// discography" is how the back catalog is fetched, on purpose).
    /// </summary>
    public DateTime FollowedAtUtc { get; set; }

    public DateTime? LastCheckedAtUtc { get; set; }

    /// <summary>When the monitor may look again. Null means as soon as it can.</summary>
    public DateTime? NextCheckAtUtc { get; set; }

    [MaxLength(1024)]
    public string? LastError { get; set; }

    public ICollection<FollowedArtistRelease> Releases { get; set; } = new List<FollowedArtistRelease>();
}

/// <summary>What the monitor decided about one release of a followed artist. Stored as an integer.</summary>
public enum FollowedReleaseDecision
{
    /// <summary>Already listed when the artist was followed — the back catalog, left alone.</summary>
    Baseline = 0,

    /// <summary>New, and its missing tracks were queued.</summary>
    Queued = 1,

    /// <summary>New, but every track was already owned or on its way.</summary>
    AlreadyOwned = 2,

    /// <summary>New, and noticed only: auto-download is off for the artist, or downloads are off.</summary>
    Noticed = 3,

    /// <summary>Appeared after the follow but is dated well before it — a catalog back-fill, not news.</summary>
    SkippedOld = 4,

    /// <summary>New, but its tracklist could not be read; retried on the next check.</summary>
    Failed = 5,
}

public static class FollowedReleaseDecisions
{
    /// <summary>
    /// The decisions that are news to the owner — a new release, whatever was done about it. The
    /// baseline, a back-fill and a failure are bookkeeping. One list, so a follow's count, the
    /// Following page and the History feed cannot disagree.
    /// </summary>
    public static readonly FollowedReleaseDecision[] News =
        [FollowedReleaseDecision.Queued, FollowedReleaseDecision.AlreadyOwned, FollowedReleaseDecision.Noticed];
}

/// <summary>
/// One release the monitor has seen for a <see cref="FollowedArtist"/>, and what it decided. The seen
/// set is what makes a check idempotent: a release with a row is never decided again, the way album
/// completion's wishlist rows tombstone an album's tracks. Keyed by <see cref="ReleaseKey"/> (kind +
/// normalized title) rather than by catalog id alone, so a re-issued edition with a new id is still
/// the release already decided.
/// </summary>
public class FollowedArtistRelease
{
    public int Id { get; set; }

    public int FollowedArtistId { get; set; }
    public FollowedArtist FollowedArtist { get; set; } = null!;

    [MaxLength(600)]
    public string ReleaseKey { get; set; } = string.Empty;

    /// <summary>The catalog id of the edition the decision was made on.</summary>
    [MaxLength(64)]
    public string DeezerAlbumId { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    public ArtistReleaseKinds Kind { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    [MaxLength(1024)]
    public string? CoverUrl { get; set; }

    public FollowedReleaseDecision Decision { get; set; }

    public int TracksQueued { get; set; }

    /// <summary>The canonical album the queued tracks hang off, when any were queued.</summary>
    public int? CanonicalAlbumId { get; set; }

    public DateTime DecidedAtUtc { get; set; }
}

/// <summary>How an <see cref="ArtistCatalogLink"/> came to name its catalog artist.</summary>
public enum ArtistCatalogLinkStatus
{
    /// <summary>A catalog artist of that name lists a release the owner owns — proven.</summary>
    Linked = 0,

    /// <summary>Picked by a person from the candidates.</summary>
    Chosen = 1,

    /// <summary>Candidates exist, but none lists anything the owner owns. Nothing is shown as theirs.</summary>
    Unconfirmed = 2,

    /// <summary>No catalog artist resembles the name.</summary>
    NotFound = 3,
}

/// <summary>
/// Which catalog artist a library artist name is, so the artist page can list a discography. Catalog
/// data keyed by the normalized name, the way <see cref="CanonicalAlbum"/> is keyed — but only a
/// <see cref="ArtistCatalogLinkStatus.Linked"/> or <see cref="ArtistCatalogLinkStatus.Chosen"/> row is
/// trusted: a search always answers, and a wrong artist here would offer a stranger's whole catalog
/// for download (see <c>ArtistCatalogResolver</c>).
/// </summary>
public class ArtistCatalogLink
{
    public int Id { get; set; }

    [MaxLength(512)]
    public string ArtistKey { get; set; } = string.Empty;

    public ArtistCatalogLinkStatus Status { get; set; }

    [MaxLength(64)]
    public string? DeezerArtistId { get; set; }

    [MaxLength(512)]
    public string? Name { get; set; }

    [MaxLength(1024)]
    public string? PictureUrl { get; set; }

    public int Fans { get; set; }

    public DateTime CheckedAtUtc { get; set; }

    /// <summary>When an unproven answer may be looked for again. Null on a trusted link.</summary>
    public DateTime? NextCheckAfterUtc { get; set; }
}
