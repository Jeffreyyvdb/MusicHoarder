using System.Diagnostics;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

/// <summary>What a local mp4 turned out to be, using the same bands as the pre-download probe.</summary>
public record MusicVideoFileMotion(
    MusicVideoMotion Motion,
    double MedianFrameDelta,
    double MaxFrameDelta,
    int FramesSampled);

public interface IMusicVideoFileAnalyzer
{
    /// <summary>
    /// Measures a music video already on disk. Null when the file cannot be read or yields too few
    /// frames to compare — never a guess.
    /// </summary>
    Task<MusicVideoFileMotion?> AnalyzeAsync(string filePath, CancellationToken ct);
}

/// <summary>
/// The on-disk counterpart of <see cref="MusicVideoProbe"/>, for videos that were downloaded before
/// anything checked them. It answers the same question with the same arithmetic and the same
/// thresholds — the only difference is where the frames come from: ffmpeg decoding the local file
/// instead of a storyboard fetched from YouTube.
///
/// <para>
/// Only keyframes are decoded (<c>-skip_frame nokey</c>), which is what keeps this cheap: a
/// four-minute clip is measured in about a tenth of a second because the decoder skips everything
/// between keyframes. Keyframes are also spread across the whole runtime, so the sample has the same
/// shape as a storyboard's.
/// </para>
/// </summary>
public class MusicVideoFileAnalyzer(
    IOptions<MusicEnricherOptions> options,
    ILogger<MusicVideoFileAnalyzer> logger) : IMusicVideoFileAnalyzer
{
    /// <summary>Frames are scaled to this before comparison — the storyboard sheets the thresholds were calibrated on are the same size.</summary>
    internal const int FrameWidth = 48;
    internal const int FrameHeight = 27;

    /// <summary>
    /// Fewer keyframes than this and the sample says nothing: a video encoded with one keyframe
    /// every 30 s gives a handful of frames, and two of them agreeing is not evidence of stillness.
    /// </summary>
    internal const int MinimumFrames = 6;

    public async Task<MusicVideoFileMotion?> AnalyzeAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var frames = await DecodeKeyframesAsync(filePath, ct);
            return Measure(frames);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing ffmpeg or an unreadable container reports "no opinion"; the audit lists the
            // video as unmeasured rather than dropping it.
            logger.LogDebug(ex, "Music video file analysis failed");
            return null;
        }
    }

    /// <summary>Reduces a flat run of same-sized grayscale frames to a verdict. Null below <see cref="MinimumFrames"/>.</summary>
    internal static MusicVideoFileMotion? Measure(byte[] frames)
    {
        var frameSize = FrameWidth * FrameHeight;
        var count = frames.Length / frameSize;
        if (count < MinimumFrames)
            return null;

        // The frames arrive stacked vertically, which is exactly a one-column sprite sheet — so the
        // storyboard measurement is reused verbatim rather than reimplemented against the same
        // thresholds.
        var measured = MusicVideoProbe.MeasureFrames(
            frames.AsSpan(0, count * frameSize).ToArray(),
            sheetWidth: FrameWidth,
            sheetHeight: FrameHeight * count,
            rows: count,
            columns: 1,
            tileWidth: FrameWidth,
            tileHeight: FrameHeight,
            usableTiles: count);
        if (measured is null)
            return null;

        return new MusicVideoFileMotion(
            MusicVideoProbe.Classify(measured.Value.Median, measured.Value.Max),
            measured.Value.Median,
            measured.Value.Max,
            count);
    }

    private async Task<byte[]> DecodeKeyframesAsync(string filePath, CancellationToken ct)
    {
        var ffmpeg = string.IsNullOrWhiteSpace(options.Value.FfmpegPath)
            ? "ffmpeg"
            : options.Value.FfmpegPath;

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        // Before -i: the decoder drops non-keyframes instead of reconstructing them.
        psi.ArgumentList.Add("-skip_frame");
        psi.ArgumentList.Add("nokey");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale={FrameWidth}:{FrameHeight}");
        // Emit every decoded frame; without this ffmpeg re-times them to a constant rate and
        // duplicates frames, which would read as stillness.
        psi.ArgumentList.Add("-fps_mode");
        psi.ArgumentList.Add("passthrough");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("gray");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-");

        using var process = new Process { StartInfo = psi };
        process.Start();
        using var buffer = new MemoryStream();
        // Drain stderr concurrently: skipping frames emits benign decoder warnings, and a full pipe
        // would deadlock the copy below.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        await Task.WhenAll(stderrTask, process.WaitForExitAsync(ct));
        return buffer.ToArray();
    }
}
