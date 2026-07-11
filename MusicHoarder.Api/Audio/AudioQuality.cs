using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Audio;

/// <summary>
/// Codec family ranked by fidelity ceiling. The tier dominates any bitrate comparison: a lossy
/// file can never outrank a lossless one no matter how high its bitrate.
/// </summary>
public enum AudioCodecTier
{
    Unknown = 0,
    Lossy = 1,
    LosslessUncompressed = 3,
    Lossless = 4,
}

/// <summary>
/// Single source of truth for comparing audio file quality across the app (duplicate election,
/// Soulseek candidate ranking, sync existence checks, upgrade decisions). Codec tier is the
/// dominant factor; bitrate only breaks ties within a tier.
/// </summary>
public static class AudioQuality
{
    // Bitrate contributes at most BitrateCap - 1 to the score, so one tier step always outweighs
    // any bitrate difference.
    private const int BitrateCap = 100_000;

    public static AudioCodecTier TierFor(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".flac" => AudioCodecTier.Lossless,
        ".wav" => AudioCodecTier.LosslessUncompressed,
        ".aiff" => AudioCodecTier.LosslessUncompressed,
        ".mp3" => AudioCodecTier.Lossy,
        ".m4a" => AudioCodecTier.Lossy,
        ".aac" => AudioCodecTier.Lossy,
        ".ogg" => AudioCodecTier.Lossy,
        ".opus" => AudioCodecTier.Lossy,
        ".wma" => AudioCodecTier.Lossy,
        _ => AudioCodecTier.Unknown,
    };

    // v2: fold in SampleRate/BitDepth (not persisted today) to discriminate 24-bit from 16-bit FLAC.
    public static int Score(string? extension, int? bitrate) =>
        (int)TierFor(extension) * BitrateCap + Math.Clamp(bitrate ?? 0, 0, BitrateCap - 1);

    public static int Score(SongMetadata song) => Score(song.Extension, song.Bitrate);
}
