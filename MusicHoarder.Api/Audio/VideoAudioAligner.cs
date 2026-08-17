using System.Numerics;

namespace MusicHoarder.Api.Audio;

/// <summary>
/// Best-offset alignment between two raw Chromaprint sub-fingerprint streams, in frames.
/// <see cref="OffsetFrames"/> is where the <em>song's</em> frame 0 lines up inside the video's
/// stream: positive = the video starts earlier (cinematic intro), negative = the song starts earlier.
/// </summary>
public record VideoAlignmentResult(int OffsetFrames, double BitErrorRate);

/// <summary>
/// Estimates the time offset between a library song's audio and a music video's audio track by
/// cross-correlating their Chromaprint fingerprints: for every candidate frame offset, XOR the
/// overlapping 32-bit sub-fingerprints and count bit errors; the true alignment shows up as a sharp
/// bit-error-rate minimum (~0.05–0.15 for the same recording; unrelated audio hovers around ~0.5).
/// A generalization of <see cref="ChromaprintComparer.Similarity(uint[], uint[], int)"/>'s small
/// sliding window to intro/outro-sized offsets. Pure math — callers convert frames→ms using the
/// measured fingerprint frame rate (≈8.08 frames/s, i.e. ~124 ms/frame).
/// </summary>
public static class VideoAudioAligner
{
    /// <summary>
    /// Returns the minimum-bit-error alignment of <paramref name="songFrames"/> against
    /// <paramref name="videoFrames"/> within ±<paramref name="maxOffsetFrames"/>, or null when the
    /// streams never overlap by at least <paramref name="minOverlapFrames"/> (~10 s by default) —
    /// too little audio in common for the minimum to mean anything.
    /// </summary>
    public static VideoAlignmentResult? EstimateOffset(
        uint[] songFrames, uint[] videoFrames, int maxOffsetFrames, int minOverlapFrames = 80)
    {
        if (songFrames.Length == 0 || videoFrames.Length == 0 || maxOffsetFrames < 0)
            return null;

        VideoAlignmentResult? best = null;
        for (var offset = -maxOffsetFrames; offset <= maxOffsetFrames; offset++)
        {
            // songFrames[i] aligns with videoFrames[i + offset].
            var (si, vi) = offset >= 0 ? (0, offset) : (-offset, 0);
            var n = Math.Min(songFrames.Length - si, videoFrames.Length - vi);
            if (n < minOverlapFrames)
                continue;

            long bitErrors = 0;
            for (var k = 0; k < n; k++)
                bitErrors += BitOperations.PopCount(songFrames[si + k] ^ videoFrames[vi + k]);

            var ber = bitErrors / (double)(n * 32);
            if (best is null || ber < best.BitErrorRate)
                best = new VideoAlignmentResult(offset, ber);
        }
        return best;
    }
}
