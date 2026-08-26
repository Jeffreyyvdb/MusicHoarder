using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// The on-disk analyzer reuses the storyboard probe's arithmetic and thresholds, so these tests
/// cover what is genuinely its own: turning ffmpeg's flat run of frames into a verdict, and refusing
/// to answer when the sample is too thin to mean anything.
/// </summary>
public class MusicVideoFileAnalyzerTests
{
    private const int FrameSize = 48 * 27;

    [Fact]
    public void Measure_IdenticalFrames_IsAStillImage()
    {
        var result = MusicVideoFileAnalyzer.Measure(Frames(_ => 90, count: 20));

        Assert.Equal(MusicVideoMotion.Static, result!.Motion);
        Assert.Equal(0, result.MedianFrameDelta);
        Assert.Equal(20, result.FramesSampled);
    }

    [Fact]
    public void Measure_FramesThatKeepChanging_IsARealVideo()
    {
        var result = MusicVideoFileAnalyzer.Measure(Frames(i => (byte)(i % 2 == 0 ? 20 : 200), count: 20));

        Assert.Equal(MusicVideoMotion.RealVideo, result!.Motion);
    }

    [Fact]
    public void Measure_TooFewKeyframes_RefusesToGuess()
    {
        // A long video encoded with sparse keyframes yields a handful of frames; two of them
        // matching is not evidence that the picture never moves.
        Assert.Null(MusicVideoFileAnalyzer.Measure(Frames(_ => 90, count: MusicVideoFileAnalyzer.MinimumFrames - 1)));
    }

    [Fact]
    public void Measure_EmptyOutput_ReturnsNull()
    {
        Assert.Null(MusicVideoFileAnalyzer.Measure([]));
    }

    [Fact]
    public void Measure_TrailingPartialFrame_IsIgnored()
    {
        // ffmpeg killed mid-write leaves an incomplete final frame; it must not shift the grid.
        var frames = Frames(_ => 90, count: 10);
        var truncated = new byte[frames.Length + 17];
        frames.CopyTo(truncated, 0);

        var result = MusicVideoFileAnalyzer.Measure(truncated);

        Assert.Equal(10, result!.FramesSampled);
        Assert.Equal(MusicVideoMotion.Static, result.Motion);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingFile_ReturnsNullWithoutRunningFfmpeg()
    {
        var analyzer = new MusicVideoFileAnalyzer(
            Microsoft.Extensions.Options.Options.Create(new MusicHoarder.Api.Options.MusicEnricherOptions
            {
                SourceDirectory = "/tmp",
                DestinationDirectory = "/tmp",
                FfmpegPath = "/nonexistent/ffmpeg", // would throw if it were ever started
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicVideoFileAnalyzer>.Instance);

        Assert.Null(await analyzer.AnalyzeAsync("/no/such/video.mp4", CancellationToken.None));
    }

    /// <summary>A flat run of <paramref name="count"/> frames, each a single luma value.</summary>
    private static byte[] Frames(Func<int, byte> value, int count)
    {
        var bytes = new byte[FrameSize * count];
        for (var frame = 0; frame < count; frame++)
            Array.Fill(bytes, value(frame), frame * FrameSize, FrameSize);
        return bytes;
    }
}
