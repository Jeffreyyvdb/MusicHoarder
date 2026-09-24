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

    // ── Mattes ────────────────────────────────────────────────────────────────
    private const int Grid = MusicVideoFileAnalyzer.MatteGridSize;

    [Fact]
    public void MeasureMatte_Letterboxed_ReportsTheBarsPlusTheirEdgeRow()
    {
        // A 2.39:1 film in a 16:9 frame: 20 black rows top and bottom on the 160-row grid. The row
        // the real edge falls in is part black, so each bar is widened by one row.
        var frames = MatteFrames(20, (_, x, y) => y is >= 20 and < Grid - 20 ? Picture(x, y) : (byte)0);

        var matte = MusicVideoFileAnalyzer.MeasureMatte(frames);

        Assert.Equal(21.0 / Grid, matte!.Letterbox);
        Assert.Equal(0, matte.Pillarbox);
    }

    [Fact]
    public void MeasureMatte_Pillarboxed_ReportsSideBars()
    {
        // A 4:3 clip in a 16:9 frame: a black 20-column bar on either side.
        var frames = MatteFrames(20, (_, x, y) => x is >= 20 and < Grid - 20 ? Picture(x, y) : (byte)0);

        var matte = MusicVideoFileAnalyzer.MeasureMatte(frames);

        Assert.Equal(0, matte!.Letterbox);
        Assert.Equal(21.0 / Grid, matte.Pillarbox);
    }

    [Fact]
    public void MeasureMatte_FullFramePicture_HasNoBars()
    {
        Assert.Equal(MusicVideoMatte.None, MusicVideoFileAnalyzer.MeasureMatte(MatteFrames(20, (_, x, y) => Picture(x, y))));
    }

    [Fact]
    public void MeasureMatte_CompressionNoiseInTheBars_IsStillBlack()
    {
        var frames = MatteFrames(20, (_, x, y) =>
            y is >= 20 and < Grid - 20 ? Picture(x, y) : (byte)((x + y) % 3 * 8)); // 0–16

        Assert.Equal(21.0 / Grid, MusicVideoFileAnalyzer.MeasureMatte(frames)!.Letterbox);
    }

    [Fact]
    public void MeasureMatte_BlackFrames_DoNotCount()
    {
        // A fade in and out: all-black keyframes say nothing about where the bars are, so they
        // neither vote for bars nor dilute the letterboxed frames.
        var frames = MatteFrames(30, (frame, x, y) =>
            frame < 10 || frame >= 25 ? (byte)0 : y is >= 20 and < Grid - 20 ? Picture(x, y) : (byte)0);

        Assert.Equal(21.0 / Grid, MusicVideoFileAnalyzer.MeasureMatte(frames)!.Letterbox);
    }

    [Fact]
    public void MeasureMatte_DarkSceneAtOneEdge_IsNotABar()
    {
        // Dark along the top only (a night sky): a bar pair is only as thick as its thinner side.
        var frames = MatteFrames(20, (_, x, y) => y >= 30 ? Picture(x, y) : (byte)0);

        Assert.Equal(0, MusicVideoFileAnalyzer.MeasureMatte(frames)!.Letterbox);
    }

    [Fact]
    public void MeasureMatte_PictureThatReachesTheEdgeNowAndThen_IsNotABar()
    {
        // Dark rows along both edges in most frames, but picture right up to the edge in a few of
        // them: rows that ever carry picture often enough are picture.
        var frames = MatteFrames(20, (frame, x, y) =>
            frame % 5 == 0 || y is >= 20 and < Grid - 20 ? Picture(x, y) : (byte)0);

        Assert.Equal(0, MusicVideoFileAnalyzer.MeasureMatte(frames)!.Letterbox);
    }

    [Fact]
    public void MeasureMatte_ACaptionInsideABar_DoesNotEraseIt()
    {
        // One keyframe in 40 has a credit line inside the bottom bar.
        var frames = MatteFrames(40, (frame, x, y) =>
            y is >= 20 and < Grid - 20 ? Picture(x, y)
            : frame == 7 && y is >= Grid - 12 and < Grid - 8 && x is > 40 and < 120 ? (byte)230
            : (byte)0);

        Assert.Equal(21.0 / Grid, MusicVideoFileAnalyzer.MeasureMatte(frames)!.Letterbox);
    }

    [Fact]
    public void MeasureMatte_MostlyDarkVideo_IsNotCropped()
    {
        // Only a band through the middle is ever lit: bars that thick are a dark video, and cropping
        // them would blow a sliver of picture up to fill the screen.
        var frames = MatteFrames(20, (_, x, y) => y is >= 65 and < 95 ? Picture(x, y) : (byte)0);

        Assert.Equal(0, MusicVideoFileAnalyzer.MeasureMatte(frames)!.Letterbox);
    }

    [Fact]
    public void MeasureMatte_TooFewLitFrames_ReportsNoBars()
    {
        var frames = MatteFrames(
            MusicVideoFileAnalyzer.MinimumMatteFrames - 1,
            (_, x, y) => y is >= 20 and < Grid - 20 ? Picture(x, y) : (byte)0);

        Assert.Equal(MusicVideoMatte.None, MusicVideoFileAnalyzer.MeasureMatte(frames));
    }

    [Fact]
    public void MeasureMatte_NothingDecoded_ReturnsNull()
    {
        // Unreadable, not "no bars": left unmeasured so a later pass can retry.
        Assert.Null(MusicVideoFileAnalyzer.MeasureMatte([]));
    }

    [Fact]
    public async Task MeasureMatteAsync_MissingFile_ReturnsNullWithoutRunningFfmpeg()
    {
        var analyzer = new MusicVideoFileAnalyzer(
            Microsoft.Extensions.Options.Options.Create(new MusicHoarder.Api.Options.MusicEnricherOptions
            {
                SourceDirectory = "/tmp",
                DestinationDirectory = "/tmp",
                FfmpegPath = "/nonexistent/ffmpeg",
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicVideoFileAnalyzer>.Instance);

        Assert.Null(await analyzer.MeasureMatteAsync("/no/such/video.mp4", CancellationToken.None));
    }

    /// <summary>A picture cell: bright, varying, never black.</summary>
    private static byte Picture(int x, int y) => (byte)(60 + (x * 7 + y * 13) % 180);

    /// <summary><paramref name="count"/> matte-grid frames, each cell from <paramref name="cell"/>(frame, x, y).</summary>
    private static byte[] MatteFrames(int count, Func<int, int, int, byte> cell)
    {
        var bytes = new byte[Grid * Grid * count];
        for (var frame = 0; frame < count; frame++)
            for (var y = 0; y < Grid; y++)
                for (var x = 0; x < Grid; x++)
                    bytes[frame * Grid * Grid + y * Grid + x] = cell(frame, x, y);
        return bytes;
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
