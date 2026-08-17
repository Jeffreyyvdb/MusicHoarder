using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public enum MusicVideoStatus
{
    Fetching = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>
/// How the stored <see cref="SongMusicVideo.SyncOffsetMs"/> was determined, ordered roughly by trust.
/// </summary>
public enum MusicVideoSyncSource
{
    /// <summary>The song's audio was extracted from this exact video — offset is 0 by construction.</summary>
    SameSource = 0,

    /// <summary>Offset estimated by chromaprint cross-correlation of the song audio vs the video's audio.</summary>
    AutoAligned = 1,

    /// <summary>The owner nudged the offset by hand; never overwritten automatically while the audio file is unchanged.</summary>
    Manual = 2,

    /// <summary>No usable alignment (missing fingerprint, low confidence, different edit) — offset is best-effort 0.</summary>
    Unaligned = 3,
}

/// <summary>
/// A music video ("clip") fetched from YouTube for one library song, played muted behind the
/// full-screen player while the song's audio plays. The audio is always the master clock; this row
/// carries the signed offset that maps audio time onto video time
/// (<c>videoTime = audioTime + SyncOffsetMs / 1000</c> — positive when the video has a cinematic
/// intro). The offset must be re-estimated whenever the song's audio file is replaced (quality
/// upgrades), because the video may then come from a different recording/edit than the audio.
/// </summary>
public class SongMusicVideo
{
    [Key]
    public int Id { get; set; }

    /// <summary>One video per song (unique index).</summary>
    public int SongId { get; set; }
    public SongMetadata Song { get; set; } = null!;

    /// <summary>Absolute path of the downloaded mp4 under the videos directory; null while fetching or after a failed fetch.</summary>
    [MaxLength(2048)]
    public string? FilePath { get; set; }

    [MaxLength(32)]
    public string? YouTubeVideoId { get; set; }

    public int? DurationSeconds { get; set; }

    /// <summary><c>videoTime = audioTime + SyncOffsetMs / 1000</c>. Positive = the video starts earlier (intro).</summary>
    public int SyncOffsetMs { get; set; }

    public MusicVideoSyncSource SyncSource { get; set; } = MusicVideoSyncSource.Unaligned;

    /// <summary>
    /// <c>1 - bestBitErrorRate</c> from the aligner for <see cref="MusicVideoSyncSource.AutoAligned"/>;
    /// null for <see cref="MusicVideoSyncSource.SameSource"/> / <see cref="MusicVideoSyncSource.Manual"/>.
    /// </summary>
    public double? SyncConfidence { get; set; }

    public MusicVideoStatus Status { get; set; } = MusicVideoStatus.Fetching;

    [MaxLength(2048)]
    public string? LastError { get; set; }

    public DateTime FetchedAtUtc { get; set; }
}
