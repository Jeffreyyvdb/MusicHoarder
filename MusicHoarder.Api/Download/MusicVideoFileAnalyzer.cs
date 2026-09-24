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

/// <summary>
/// The black bars baked into a video's frame, as the share of the frame each bar covers:
/// <paramref name="Letterbox"/> is the height of the top bar (and of the bottom one — they are
/// measured as a pair), <paramref name="Pillarbox"/> the width of the left bar (and of the right).
/// A 2.39:1 film mastered into a 16:9 upload is about 0.13 letterbox; <see cref="None"/> is a
/// frame that is picture edge to edge.
/// </summary>
public record MusicVideoMatte(double Letterbox, double Pillarbox)
{
    public static readonly MusicVideoMatte None = new(0, 0);
}

public interface IMusicVideoFileAnalyzer
{
    /// <summary>
    /// Measures a music video already on disk. Null when the file cannot be read or yields too few
    /// frames to compare — never a guess.
    /// </summary>
    Task<MusicVideoFileMotion?> AnalyzeAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Finds the black bars baked into a video on disk, so a backdrop that fills the screen with it
    /// can crop them away. Null only when the file cannot be read (a later pass may retry); a video
    /// too dark to judge reports <see cref="MusicVideoMatte.None"/>, never a guess.
    /// </summary>
    Task<MusicVideoMatte?> MeasureMatteAsync(string filePath, CancellationToken ct);
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
///
/// <para>
/// The same keyframes answer a second question, <see cref="MeasureMatteAsync"/>: where the black
/// bars baked into the frame are, so a backdrop that fills the screen with the video can crop them.
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

    /// <summary>
    /// Frames are scaled to this square before looking for bars. The squash does not matter — only
    /// the share of each axis a bar covers is measured — and 160 rows put a bar's edge within
    /// 0.6% of the frame.
    /// </summary>
    internal const int MatteGridSize = 160;

    /// <summary>
    /// A row or column is lit in a frame when any cell in it is brighter than this (0–255). Encoded
    /// black sits well under it even after compression noise; picture, even a dark scene, rarely
    /// stays under it across a whole row of every keyframe.
    /// </summary>
    internal const byte MatteLitThreshold = 32;

    /// <summary>Fewer lit keyframes than this and there is nothing to measure bars against.</summary>
    internal const int MinimumMatteFrames = 3;

    /// <summary>
    /// A row counts as picture when it is lit in at least this share of the lit keyframes. Not every
    /// one: a caption or a credit card sometimes lands inside a bar, and one of those must not make
    /// the bar read as picture for the whole video.
    /// </summary>
    internal const double MattePictureShare = 0.05;

    /// <summary>
    /// A bar wider than this is not a bar, it is a mostly dark video; cropping it would blow a small
    /// patch of picture up to fill the screen. A 9:16 upload pillarboxed into 16:9 is 0.34.
    /// </summary>
    internal const double MaximumMatte = 0.375;

    public async Task<MusicVideoFileMotion?> AnalyzeAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var frames = await DecodeKeyframesAsync(filePath, $"scale={FrameWidth}:{FrameHeight}", ct);
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

    public async Task<MusicVideoMatte?> MeasureMatteAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            // Area averaging: a grid cell is the mean of the pixels it covers, rather than a sample
            // that could land on one stray bright pixel inside a bar.
            var frames = await DecodeKeyframesAsync(
                filePath, $"scale={MatteGridSize}:{MatteGridSize}:flags=area", ct);
            return MeasureMatte(frames);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Music video matte measurement failed");
            return null;
        }
    }

    /// <summary>
    /// Finds the bars in a flat run of <see cref="MatteGridSize"/>-square grayscale keyframes. Null
    /// when not one whole frame was decoded (the file could not be read).
    ///
    /// <para>
    /// Bars are the same in every frame and picture is not, so each row is judged across the whole
    /// video: picture when it is lit in enough of the keyframes that show anything at all (a fade to
    /// black says nothing about where the bars are). The two bars of a pair are taken as the thinner
    /// of the two, which keeps a scene that is dark along one edge from reading as a bar, and each
    /// bar is widened by one row, because the row its edge falls in is part black.
    /// </para>
    /// </summary>
    internal static MusicVideoMatte? MeasureMatte(byte[] frames)
    {
        const int size = MatteGridSize;
        const int frameSize = size * size;
        var count = frames.Length / frameSize;
        if (count == 0)
            return null;

        var rowHits = new int[size];
        var columnHits = new int[size];
        var rowLit = new bool[size];
        var columnLit = new bool[size];
        var litFrames = 0;
        for (var frame = 0; frame < count; frame++)
        {
            Array.Clear(rowLit);
            Array.Clear(columnLit);
            var any = false;
            var offset = frame * frameSize;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (frames[offset + y * size + x] <= MatteLitThreshold)
                        continue;
                    rowLit[y] = true;
                    columnLit[x] = true;
                    any = true;
                }
            }

            if (!any)
                continue;
            litFrames++;
            for (var i = 0; i < size; i++)
            {
                if (rowLit[i]) rowHits[i]++;
                if (columnLit[i]) columnHits[i]++;
            }
        }

        if (litFrames < MinimumMatteFrames)
            return MusicVideoMatte.None;

        var needed = Math.Max(1, (int)Math.Ceiling(litFrames * MattePictureShare));
        return new MusicVideoMatte(BarShare(rowHits, needed), BarShare(columnHits, needed));
    }

    /// <summary>The share of the axis each bar of a pair covers, from per-row (or column) hit counts.</summary>
    private static double BarShare(int[] hits, int needed)
    {
        var first = Array.FindIndex(hits, h => h >= needed);
        if (first < 0)
            return 0;
        var last = Array.FindLastIndex(hits, h => h >= needed);

        var bar = Math.Min(first, hits.Length - 1 - last);
        if (bar == 0)
            return 0;

        var share = (double)(bar + 1) / hits.Length;
        return share > MaximumMatte ? 0 : share;
    }

    /// <param name="scale">The ffmpeg scale filter that sizes each frame.</param>
    private async Task<byte[]> DecodeKeyframesAsync(string filePath, string scale, CancellationToken ct)
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
        psi.ArgumentList.Add(scale);
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
