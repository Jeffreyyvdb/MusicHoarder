using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Lifecycle of a single wishlisted track.
/// <c>Pending → Downloading → Downloaded | Failed | NotFound</c>, or <c>SkippedOwned</c> when the track
/// is already in the local library (an exact <c>InLibrary</c> match in the Spotify match cache).
/// <para>
/// <see cref="Failed"/> is terminal only when <see cref="WishlistItem.NextAttemptAtUtc"/> is null. With a
/// timestamp set the row is a scheduled retry: the download sweep claims it again once that time has
/// passed (exponential backoff, capped by <c>MusicEnricher:WishlistDownloadMaxAttempts</c>; a
/// provider that was merely unreachable never consumes an attempt). A manual retry always resets both.
/// </para>
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
    /// <summary>The owner asked for it — Spotify liked songs, a playlist, a Deezer discover list, a YouTube playlist, a URL import.</summary>
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
    /// <see cref="WishlistItemStatus.NotFound"/> rows act as permanent tombstones. (A Failed row with a
    /// scheduled <see cref="NextAttemptAtUtc"/> is still in flight, not a tombstone — the cross-album
    /// claim check treats it as such.)
    /// <para>
    /// Deliberately keyed to the album and not to a <see cref="CanonicalAlbumTrack"/>:
    /// <c>CanonicalAlbumFetchService.UpsertReconciled</c> deletes and recreates every track row on each
    /// re-fetch, so a per-track FK would null itself out and re-open every tombstone.
    /// </para>
    /// </summary>
    public int? CanonicalAlbumId { get; set; }
    public CanonicalAlbum? CanonicalAlbum { get; set; }

    /// <summary>
    /// Spotify track id — the acquisition key the lossless download provider resolves to a source, not
    /// just provenance. Carried over from the source for Spotify-sourced items, and resolved from the
    /// canonical tracklist for <see cref="WishlistItemOrigin.AlbumCompletion"/> ones
    /// (<c>IAlbumCompletionIdentityResolver</c>). Null for Deezer-sourced items with no resolved
    /// Spotify equivalent, and for anything the resolver could not identify confidently.
    /// </summary>
    [MaxLength(64)]
    public string? SpotifyTrackId { get; set; }

    /// <summary>Deezer track id; set for items sourced from a Deezer discover playlist.</summary>
    [MaxLength(64)]
    public string? DeezerTrackId { get; set; }

    /// <summary>
    /// Direct source URL for items tied to one exact video: a pasted YouTube link, or an entry of a
    /// synced YouTube playlist. When set, the downloader fetches this exact URL instead of searching by
    /// artist/title — the only way to acquire a specific YouTube remix/edit that has no
    /// Spotify/streaming equivalent. Null for Spotify- and Deezer-sourced items, which resolve by
    /// identity through the provider chain.
    /// </summary>
    [MaxLength(2048)]
    public string? SourceUrl { get; set; }

    /// <summary>
    /// The YouTube video id behind <see cref="SourceUrl"/>, when that is a YouTube video. The dedupe key
    /// for YouTube playlist sync, the same way <see cref="SpotifyTrackId"/> and
    /// <see cref="DeezerTrackId"/> are for their sources. Items imported before this column existed
    /// carry the id only inside <see cref="SourceUrl"/>, and the sync reads it from there.
    /// </summary>
    [MaxLength(32)]
    public string? YouTubeVideoId { get; set; }

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

    /// <summary>
    /// Set when the file came from a provider positioned <em>after</em> one that was unreachable at the
    /// time (e.g. yt-dlp delivered because the spotiflac sidecar was mid-redeploy): the name of the
    /// preferred provider that was skipped. Provenance only — it is never cleared. The automatic quality
    /// upgrade sweep uses it to offer the song to that provider first once it is back.
    /// </summary>
    [MaxLength(64)]
    public string? FallbackFromProvider { get; set; }

    /// <summary>
    /// Earliest time the download sweep may claim this <see cref="WishlistItemStatus.Failed"/> row again.
    /// Null on a Failed row means parked (attempt cap reached, or a legacy failure) until a manual retry.
    /// Ignored for every other status.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>Absolute path of the downloaded file under the source directory, once fetched.</summary>
    [MaxLength(2048)]
    public string? DownloadedFilePath { get; set; }

    /// <summary>
    /// Per-item override of <c>MusicEnricher:DownloadMusicVideos</c>: the "Also download the music
    /// video" choice made in the import dialog. Null (playlist-synced items, older rows) follows the
    /// server flag.
    /// </summary>
    public bool? DownloadMusicVideo { get; set; }

    /// <summary>
    /// Absolute path of the companion music video (mp4), when the item's effective music-video
    /// setting (<see cref="DownloadMusicVideo"/> ?? <c>MusicEnricher:DownloadMusicVideos</c>) was on
    /// for this download. Carried here until the scanner ingests the audio file and
    /// <c>LinkDownloadedItemsAsync</c> promotes it to a <see cref="SongMusicVideo"/> row.
    /// </summary>
    [MaxLength(2048)]
    public string? DownloadedVideoFilePath { get; set; }

    [MaxLength(32)]
    public string? DownloadedVideoYouTubeId { get; set; }

    /// <summary>
    /// True when the audio was extracted by yt-dlp from the <em>same</em> YouTube video the clip was
    /// downloaded from — sync offset is then 0 by construction (<see cref="MusicVideoSyncSource.SameSource"/>).
    /// </summary>
    public bool DownloadedVideoIsSameSource { get; set; }

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

    /// <summary>Longest backoff between two automatic attempts, whatever the attempt count.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(24);

    /// <summary>
    /// A provider was reached and the attempt genuinely failed: count it, and schedule the next attempt
    /// with exponential backoff (<paramref name="retryBaseMinutes"/> · 2^(attempts-1), capped at
    /// <see cref="MaxRetryDelay"/>). Once <paramref name="maxAttempts"/> is reached the row parks
    /// (<see cref="NextAttemptAtUtc"/> null) until a manual <see cref="Requeue"/>.
    /// </summary>
    public void MarkFailed(string? error, int retryBaseMinutes, int maxAttempts, DateTime now)
    {
        Status = WishlistItemStatus.Failed;
        LastError = error;
        AttemptCount += 1;
        NextAttemptAtUtc = AttemptCount >= maxAttempts
            ? null
            : now + Backoff(retryBaseMinutes, AttemptCount);
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// No provider could be reached (the preferred one was down and nothing after it produced a file):
    /// nothing was tried against the track, so the attempt count is untouched and the row simply waits
    /// one base delay. An outage of any length can therefore never park an item.
    /// </summary>
    public void Defer(string provider, string? error, int retryBaseMinutes, DateTime now)
    {
        Status = WishlistItemStatus.Failed;
        LastError = $"{provider} unavailable: {error ?? "unreachable"}";
        NextAttemptAtUtc = now + TimeSpan.FromMinutes(retryBaseMinutes);
        UpdatedAtUtc = now;
    }

    /// <summary>Manual retry: back to Pending with a clean slate, so the next sweep tries the whole chain.</summary>
    public void Requeue(DateTime now)
    {
        Status = WishlistItemStatus.Pending;
        AttemptCount = 0;
        NextAttemptAtUtc = null;
        LastError = null;
        UpdatedAtUtc = now;
    }

    private static TimeSpan Backoff(int baseMinutes, int attempt)
    {
        var minutes = baseMinutes * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromMinutes(Math.Min(MaxRetryDelay.TotalMinutes, minutes));
    }
}
