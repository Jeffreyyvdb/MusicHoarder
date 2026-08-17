using MusicHoarder.Api.Audio;

namespace MusicHoarder.Api.Tests.Audio;

public class VideoAudioAlignerTests
{
    private static uint[] RandomFrames(int count, int seed)
    {
        var rng = new Random(seed);
        var frames = new uint[count];
        for (var i = 0; i < count; i++)
            frames[i] = (uint)rng.Next(int.MinValue, int.MaxValue);
        return frames;
    }

    /// <summary>Flips roughly <paramref name="bitNoise"/> of the bits per frame (codec/transcode noise).</summary>
    private static uint[] WithNoise(uint[] frames, double bitNoise, int seed)
    {
        var rng = new Random(seed);
        var noisy = new uint[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            for (var bit = 0; bit < 32; bit++)
                if (rng.NextDouble() < bitNoise)
                    frame ^= 1u << bit;
            noisy[i] = frame;
        }
        return noisy;
    }

    [Fact]
    public void RecoversIntroOffset()
    {
        // Video = 40-frame cinematic intro + the song's audio (with 5% bit noise) + outro.
        var song = RandomFrames(800, seed: 1);
        var video = RandomFrames(40, seed: 2)
            .Concat(WithNoise(song, 0.05, seed: 3))
            .Concat(RandomFrames(60, seed: 4))
            .ToArray();

        var result = VideoAudioAligner.EstimateOffset(song, video, maxOffsetFrames: 200);

        Assert.NotNull(result);
        Assert.InRange(result.OffsetFrames, 39, 41);
        Assert.True(result.BitErrorRate < 0.1, $"BER {result.BitErrorRate} should reflect the 5% noise");
    }

    [Fact]
    public void RecoversNegativeOffset_WhenSongStartsBeforeVideo()
    {
        // The video misses the song's first 30 frames (e.g. the clip cuts straight into verse 1).
        var song = RandomFrames(600, seed: 10);
        var video = WithNoise(song.Skip(30).ToArray(), 0.05, seed: 11);

        var result = VideoAudioAligner.EstimateOffset(song, video, maxOffsetFrames: 100);

        Assert.NotNull(result);
        Assert.InRange(result.OffsetFrames, -31, -29);
    }

    [Fact]
    public void UnrelatedAudio_HasHighBitErrorRate()
    {
        var song = RandomFrames(600, seed: 20);
        var video = RandomFrames(800, seed: 21);

        var result = VideoAudioAligner.EstimateOffset(song, video, maxOffsetFrames: 100);

        // Random 32-bit frames disagree on ~half their bits at every offset; a confidence threshold
        // of ~0.35 must reject this comfortably.
        Assert.NotNull(result);
        Assert.True(result.BitErrorRate > 0.4, $"BER {result.BitErrorRate} should look like noise");
    }

    [Fact]
    public void TooLittleOverlap_ReturnsNull()
    {
        var song = RandomFrames(40, seed: 30);
        var video = RandomFrames(40, seed: 31);

        // minOverlap of 80 frames can never be met with 40-frame streams.
        Assert.Null(VideoAudioAligner.EstimateOffset(song, video, maxOffsetFrames: 10));
    }

    [Fact]
    public void EmptyInput_ReturnsNull()
    {
        Assert.Null(VideoAudioAligner.EstimateOffset([], RandomFrames(100, 1), 10));
        Assert.Null(VideoAudioAligner.EstimateOffset(RandomFrames(100, 1), [], 10));
    }

    [Fact]
    public void ZeroMaxOffset_StillEvaluatesIdentityAlignment()
    {
        var song = RandomFrames(200, seed: 40);
        var result = VideoAudioAligner.EstimateOffset(song, song, maxOffsetFrames: 0);

        Assert.NotNull(result);
        Assert.Equal(0, result.OffsetFrames);
        Assert.Equal(0, result.BitErrorRate);
    }
}
