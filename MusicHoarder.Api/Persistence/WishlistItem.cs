using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Lifecycle of a single wishlisted track.
/// <c>Pending → Downloading → Downloaded | Failed | NotFound</c>, or <c>SkippedOwned</c> when the track
/// is already in the local library (an exact <c>InLibrary</c> match in the Spotify match cache).
/// </summary>
public enum WishlistItemStatus
{
    Pending,
    SkippedOwned,
    Downloading,
    Downloaded,
    Failed,
    NotFound,
}

/// <summary>
/// One Spotify track the owner wants to acquire. The downloader fetches it into the source directory,
/// where the existing scan→fingerprint→enrich→build pipeline ingests it like any other file. Spotify
/// metadata is denormalized so the row stands alone even after the source playlist is removed.
/// </summary>
public class WishlistItem
{
    public int Id { get; set; }

    /// <summary>Owner of this wishlist item — Spotify accounts are per-user.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// The source that introduced this item. Nullable + <c>OnDelete.SetNull</c> so removing a source
    /// keeps already-acquired tracks.
    /// </summary>
    public int? WishlistSourceId { get; set; }
    public WishlistSource? WishlistSource { get; set; }

    /// <summary>Spotify track id. Null for Deezer-sourced items with no resolved Spotify equivalent.</summary>
    [MaxLength(64)]
    public string? SpotifyTrackId { get; set; }

    /// <summary>Deezer track id; set for items sourced from a Deezer discover playlist.</summary>
    [MaxLength(64)]
    public string? DeezerTrackId { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Artist { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Album { get; set; }

    /// <summary>International Standard Recording Code, when Spotify surfaces it. Used by future ISRC-matching downloaders.</summary>
    [MaxLength(32)]
    public string? Isrc { get; set; }

    public int DurationMs { get; set; }

    [MaxLength(1024)]
    public string? AlbumArt { get; set; }

    /// <summary>When the track was added/liked on Spotify (drives "newest first" and sync diffing).</summary>
    public DateTime? SpotifyAddedAtUtc { get; set; }

    public WishlistItemStatus Status { get; set; } = WishlistItemStatus.Pending;

    /// <summary>Name of the <c>IDownloadProvider</c> that produced (or last attempted) the file, e.g. "yt-dlp".</summary>
    [MaxLength(64)]
    public string? DownloadProvider { get; set; }

    /// <summary>Absolute path of the downloaded file under the source directory, once fetched.</summary>
    [MaxLength(2048)]
    public string? DownloadedFilePath { get; set; }

    /// <summary>
    /// The ingested library song this item resolved to (linked after the scanner picks up the file, or
    /// the already-owned song for a <see cref="WishlistItemStatus.SkippedOwned"/> item).
    /// </summary>
    public int? DownloadedSongId { get; set; }
    public SongMetadata? DownloadedSong { get; set; }

    public int AttemptCount { get; set; }

    [MaxLength(2048)]
    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
