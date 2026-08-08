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
/// Who put this item on the wishlist. Mirrors <c>UpgradeRequest.Trigger</c>: a discriminator on the
/// work row telling app-generated work from user-generated work. <see cref="UserRequested"/> is
/// <c>0</c> so every pre-existing item keeps top download priority with no backfill.
/// </summary>
public enum WishlistItemOrigin
{
    /// <summary>The owner asked for it — Spotify liked songs, a playlist, a Deezer discover list, a URL import.</summary>
    UserRequested = 0,

    /// <summary>
    /// Queued by <c>AlbumCompletionSweep</c> to fill in an album the owner already holds part of.
    /// Claimed by the downloader strictly after every <see cref="UserRequested"/> item.
    /// </summary>
    AlbumCompletion = 1,
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
    /// keeps already-acquired tracks. Always null for <see cref="WishlistItemOrigin.AlbumCompletion"/>
    /// items — a <see cref="WishlistSource"/> models a remote collection with a sync loop, which album
    /// completion has none of.
    /// </summary>
    public int? WishlistSourceId { get; set; }
    public WishlistSource? WishlistSource { get; set; }

    /// <summary>Whether the owner asked for this track or album completion queued it.</summary>
    public WishlistItemOrigin Origin { get; set; } = WishlistItemOrigin.UserRequested;

    /// <summary>
    /// The album this item was queued to complete. Set only for
    /// <see cref="WishlistItemOrigin.AlbumCompletion"/> items, where it is both the provenance and the
    /// dedupe key: the sweep loads every item for an album — <em>any</em> status — and skips canonical
    /// tracks it already has a row for, so terminal <see cref="WishlistItemStatus.Failed"/> /
    /// <see cref="WishlistItemStatus.NotFound"/> rows act as permanent tombstones.
    /// <para>
    /// Deliberately keyed to the album and not to a <see cref="CanonicalAlbumTrack"/>:
    /// <c>CanonicalAlbumFetchService.UpsertReconciled</c> deletes and recreates every track row on each
    /// re-fetch, so a per-track FK would null itself out and re-open every tombstone.
    /// </para>
    /// </summary>
    public int? CanonicalAlbumId { get; set; }
    public CanonicalAlbum? CanonicalAlbum { get; set; }

    /// <summary>Spotify track id. Null for Deezer-sourced items with no resolved Spotify equivalent.</summary>
    [MaxLength(64)]
    public string? SpotifyTrackId { get; set; }

    /// <summary>Deezer track id; set for items sourced from a Deezer discover playlist.</summary>
    [MaxLength(64)]
    public string? DeezerTrackId { get; set; }

    /// <summary>
    /// Direct source URL for single-track URL imports (e.g. a pasted YouTube video). When set, the
    /// downloader fetches this exact URL instead of searching by artist/title — the only way to acquire
    /// a specific YouTube remix/edit that has no Spotify/streaming equivalent. Null for playlist-sourced
    /// items, which resolve by identity through the provider chain.
    /// </summary>
    [MaxLength(2048)]
    public string? SourceUrl { get; set; }

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
