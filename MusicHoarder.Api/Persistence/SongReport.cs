using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>What was wrong with a downloaded track. Stored as numbers: append only.</summary>
public enum SongReportReason
{
    /// <summary>A different song altogether (another artist, another track, not music).</summary>
    WrongSong = 0,

    /// <summary>The right song in the wrong form: live, remix, cover, sped up, instrumental, a karaoke cut.</summary>
    WrongVersion = 1,

    /// <summary>The right recording, badly: cut off, low quality, noise, silence, a voice-over.</summary>
    BadAudio = 2,

    /// <summary>Anything else; the note says what.</summary>
    Other = 3,
}

/// <summary>
/// The owner's verdict that a downloaded track was wrong, kept as evidence for improving how
/// downloads are found. It is a snapshot taken when the report was made — what was asked for (the
/// wishlist item), what arrived (the song's tags and file) and how (the provider and the source it
/// took the audio from) — because the report usually removes the song and resets the wishlist item,
/// and neither keeps those facts afterwards. Owner-scoped, never shown to anyone else.
/// </summary>
public class SongReport
{
    public int Id { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>The reported song. Kept after removal (the row is only soft-deleted).</summary>
    public int? SongId { get; set; }

    public SongReportReason Reason { get; set; }

    /// <summary>What the owner wrote, if anything.</summary>
    [MaxLength(1000)]
    public string? Note { get; set; }

    // ── What arrived ──────────────────────────────────────────────────────────

    [MaxLength(512)]
    public string? Title { get; set; }

    [MaxLength(512)]
    public string? Artist { get; set; }

    [MaxLength(512)]
    public string? Album { get; set; }

    public int? DurationMs { get; set; }

    [MaxLength(512)]
    public string? FileName { get; set; }

    /// <summary>The enrichment provider that named the file (<see cref="SongMetadata.MatchedBy"/>).</summary>
    [MaxLength(64)]
    public string? MatchedBy { get; set; }

    public double? MatchConfidence { get; set; }

    // ── What was asked for, and how it was fetched ────────────────────────────

    /// <summary>The wishlist item that fetched the file. A plain id: items can be removed later.</summary>
    public int? WishlistItemId { get; set; }

    public WishlistItemOrigin? Origin { get; set; }

    [MaxLength(512)]
    public string? RequestedTitle { get; set; }

    [MaxLength(512)]
    public string? RequestedArtist { get; set; }

    [MaxLength(512)]
    public string? RequestedAlbum { get; set; }

    public int? RequestedDurationMs { get; set; }

    [MaxLength(64)]
    public string? SpotifyTrackId { get; set; }

    [MaxLength(64)]
    public string? DeezerTrackId { get; set; }

    [MaxLength(32)]
    public string? Isrc { get; set; }

    /// <summary>The <c>IDownloadProvider</c> that delivered the file, e.g. "yt-dlp".</summary>
    [MaxLength(64)]
    public string? DownloadProvider { get; set; }

    /// <summary>The provider's id for the media the audio came from (yt-dlp: the YouTube video id), when known.</summary>
    [MaxLength(128)]
    public string? DownloadedSourceId { get; set; }

    /// <summary>The exact URL the item asked for (a pasted link, a YouTube playlist entry).</summary>
    [MaxLength(2048)]
    public string? SourceUrl { get; set; }

    // ── What the report did ───────────────────────────────────────────────────

    /// <summary>When the song was taken out of the library; null when the report only recorded it.</summary>
    public DateTime? RemovedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
