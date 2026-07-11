using MusicHoarder.Api.Audio;

namespace MusicHoarder.Api.Tests.Audio;

public class AudioQualityTests
{
    [Theory]
    [InlineData(".flac", AudioCodecTier.Lossless)]
    [InlineData(".FLAC", AudioCodecTier.Lossless)]
    [InlineData(".wav", AudioCodecTier.LosslessUncompressed)]
    [InlineData(".aiff", AudioCodecTier.LosslessUncompressed)]
    [InlineData(".mp3", AudioCodecTier.Lossy)]
    [InlineData(".m4a", AudioCodecTier.Lossy)]
    [InlineData(".aac", AudioCodecTier.Lossy)]
    [InlineData(".ogg", AudioCodecTier.Lossy)]
    [InlineData(".opus", AudioCodecTier.Lossy)]
    [InlineData(".wma", AudioCodecTier.Lossy)]
    [InlineData(".xyz", AudioCodecTier.Unknown)]
    [InlineData("", AudioCodecTier.Unknown)]
    [InlineData(null, AudioCodecTier.Unknown)]
    public void TierFor_MapsExtensions(string? extension, AudioCodecTier expected)
    {
        Assert.Equal(expected, AudioQuality.TierFor(extension));
    }

    [Fact]
    public void Score_TierDominatesBitrate()
    {
        // A max-bitrate lossy file must never outrank any lossless file.
        Assert.True(AudioQuality.Score(".flac", null) > AudioQuality.Score(".mp3", 320));
        Assert.True(AudioQuality.Score(".flac", 0) > AudioQuality.Score(".mp3", int.MaxValue));
        Assert.True(AudioQuality.Score(".wav", null) > AudioQuality.Score(".opus", int.MaxValue));
        Assert.True(AudioQuality.Score(".flac", 900) > AudioQuality.Score(".wav", 1411));
    }

    [Fact]
    public void Score_BitrateBreaksTiesWithinTier()
    {
        Assert.True(AudioQuality.Score(".mp3", 320) > AudioQuality.Score(".mp3", 128));
        Assert.True(AudioQuality.Score(".flac", 1411) > AudioQuality.Score(".flac", 900));
        Assert.Equal(AudioQuality.Score(".mp3", 192), AudioQuality.Score(".opus", 192));
    }

    [Fact]
    public void Score_ClampsBitrate()
    {
        Assert.Equal(AudioQuality.Score(".mp3", 99_999), AudioQuality.Score(".mp3", int.MaxValue));
        Assert.Equal(AudioQuality.Score(".mp3", 0), AudioQuality.Score(".mp3", -5));
        Assert.Equal(AudioQuality.Score(".mp3", 0), AudioQuality.Score(".mp3", null));
    }

    [Fact]
    public void Score_PreservesLegacyRelativeOrdering()
    {
        // Same ordering the old FLAC=1000 / WAV=900 / lossy=bitrate formula produced.
        var flac = AudioQuality.Score(".flac", null);
        var wav = AudioQuality.Score(".wav", null);
        var mp3High = AudioQuality.Score(".mp3", 320);
        var opusLow = AudioQuality.Score(".opus", 96);
        var unknown = AudioQuality.Score(".xyz", 320);

        Assert.True(flac > wav);
        Assert.True(wav > mp3High);
        Assert.True(mp3High > opusLow);
        Assert.True(opusLow > unknown);
    }
}
