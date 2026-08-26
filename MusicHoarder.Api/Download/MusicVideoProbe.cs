using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using SkiaSharp;

namespace MusicHoarder.Api.Download;

/// <summary>
/// How much the picture actually moves over a candidate's full runtime — the difference between a
/// music video worth showing behind the player and an audio upload that paints one album cover for
/// three minutes.
/// </summary>
public enum MusicVideoMotion
{
    /// <summary>No storyboard was available, or the probe failed. Never used to reject a candidate.</summary>
    Unknown = 0,

    /// <summary>The frames do not change: an album cover, a single still, an "Official Audio" upload.</summary>
    Static = 1,

    /// <summary>Mostly still with occasional changes: lyric cards, a slideshow, a looping visualizer.</summary>
    LowMotion = 2,

    /// <summary>Continuous motion across the runtime — an actual clip.</summary>
    RealVideo = 3,
}

/// <summary>
/// What a probe learned about one YouTube video without downloading it.
/// <paramref name="EstimatedBytes"/> is the size the real fetch would write, so the owner can weigh
/// the disk cost before spending it.
/// </summary>
public record MusicVideoProbeResult(
    string VideoId,
    string Title,
    string Channel,
    int? DurationSeconds,
    MusicVideoMotion Motion,
    long? EstimatedBytes,
    double? MedianFrameDelta,
    double? MaxFrameDelta,
    bool SquareSource,
    string? ThumbnailUrl,
    string? Error = null);

public interface IMusicVideoProbe
{
    /// <summary>
    /// Inspects a candidate without downloading it: one yt-dlp metadata call plus one storyboard
    /// sprite sheet (tens of KB) against tens of MB for the video. Never throws for an unusable
    /// candidate — it reports <see cref="MusicVideoMotion.Unknown"/> instead, because a probe
    /// failure must not be able to veto a fetch.
    /// </summary>
    Task<MusicVideoProbeResult> ProbeAsync(string videoIdOrUrl, CancellationToken ct);
}

/// <summary>
/// Classifies a YouTube candidate as a real clip, a low-motion lyric/slideshow upload, or a static
/// cover image — before any video bytes are downloaded.
///
/// <para>
/// The signal is YouTube's own storyboard: a sprite sheet of thumbnails sampled across the whole
/// video, served for the scrubbing preview. The top storyboard level covers the entire runtime in a
/// single ~20-30 KB request (YouTube picks its frame rate so <c>duration * fps</c> exactly fills one
/// sheet), which makes "does this picture ever change" answerable for the price of a thumbnail.
/// </para>
///
/// <para>
/// Measured against real uploads (mean absolute luma difference between consecutive frames):
/// album-cover uploads — an auto-generated "- Topic" art track, a label's "Official Audio" — all
/// score a median of 0.00, meaning most consecutive frames are byte-identical. Lyric videos land
/// between 3 and 15. Actual music videos land between 24 and 42. The bands are separated by an
/// order of magnitude, which is what makes fixed thresholds safe here.
/// </para>
/// </summary>
public class MusicVideoProbe(
    IOptions<MusicEnricherOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<MusicVideoProbe> logger) : IMusicVideoProbe
{
    /// <summary>
    /// Below this median frame delta the picture is not changing at all. The highest static sample
    /// measured 0.33 and the lowest moving sample 3.38, so this sits in an empty band.
    /// </summary>
    internal const double StaticMedianDelta = 1.5;

    /// <summary>
    /// A still image cut between a handful of others (a two-slide "video") has a near-zero median
    /// but large jumps at the cuts. Those are a slideshow, not a static cover.
    /// </summary>
    internal const double StaticMaxDelta = 40.0;

    /// <summary>Continuous motion. Real clips measured 24-42; lyric videos 3-15.</summary>
    internal const double RealVideoMedianDelta = 10.0;

    /// <summary>Cap on pixels sampled per frame. The selected sheet has ~48x27 tiles, so this rarely binds.</summary>
    private const int MaxSampledPixelsPerFrame = 2048;

    public async Task<MusicVideoProbeResult> ProbeAsync(string videoIdOrUrl, CancellationToken ct)
    {
        var url = MusicVideoDownloader.CanonicalizePin(videoIdOrUrl);
        var videoId = url is not null && ImportUrlParser.TryParse(url, out _, out var parsed) ? parsed : videoIdOrUrl;
        if (url is null)
            return Unknown(videoId, "not a usable YouTube id or URL");

        var opts = options.Value;
        var cookiesPath = YtDlpCookies.PrepareWritableCopy(opts.YtDlpCookiesPath, logger);
        try
        {
            var psi = YtDlpProcess.Create(opts, cookiesPath, includeThrottle: false);
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("-J");
            psi.ArgumentList.Add(url);

            var (_, stdout, stderr) = await YtDlpProcess.RunAsync(psi, ct);
            if (string.IsNullOrWhiteSpace(stdout))
            {
                logger.LogInformation("Music video probe returned no metadata for {VideoId}: {Error}",
                    LogSanitizer.ForLog(videoId), LogSanitizer.ForLog(Truncate(stderr)));
                return Unknown(videoId, stderr.Length == 0 ? "no metadata" : Truncate(stderr));
            }

            var metadata = ParseMetadata(stdout, opts.MusicVideoMaxHeight);
            if (metadata is null)
                return Unknown(videoId, "unparseable metadata");

            var motionSheet = SelectMotionSheet(metadata.Storyboards);
            if (motionSheet is null)
            {
                // No storyboard (very new or very short uploads) — report what metadata gave us and
                // leave the verdict Unknown rather than guessing from bitrate, which overlaps badly
                // between a long art track and a short lyric video.
                return new MusicVideoProbeResult(
                    videoId, metadata.Title, metadata.Channel, metadata.DurationSeconds,
                    MusicVideoMotion.Unknown, metadata.EstimatedBytes, null, null,
                    IsSquareSource(metadata.Storyboards), metadata.ThumbnailUrl, "no storyboard available");
            }

            var measurement = await MeasureAsync(motionSheet, metadata.DurationSeconds, ct);
            var motion = measurement is null
                ? MusicVideoMotion.Unknown
                : Classify(measurement.Value.Median, measurement.Value.Max);

            return new MusicVideoProbeResult(
                videoId,
                metadata.Title,
                metadata.Channel,
                metadata.DurationSeconds,
                motion,
                metadata.EstimatedBytes,
                measurement?.Median,
                measurement?.Max,
                IsSquareSource(metadata.Storyboards),
                metadata.ThumbnailUrl,
                measurement is null ? "storyboard unreadable" : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing binary or a YouTube hiccup must degrade to "no opinion", never to a veto.
            logger.LogWarning(ex, "Music video probe failed for {VideoId}", LogSanitizer.ForLog(videoId));
            return Unknown(videoId, ex.Message);
        }
        finally
        {
            YtDlpCookies.Cleanup(cookiesPath, opts.YtDlpCookiesPath);
        }
    }

    private static MusicVideoProbeResult Unknown(string videoId, string error) =>
        new(videoId, "", "", null, MusicVideoMotion.Unknown, null, null, null, false, null, error);

    /// <summary>Fetches the sprite sheet and reduces it to a median/max consecutive-frame difference.</summary>
    private async Task<(double Median, double Max)?> MeasureAsync(
        Storyboard sheet, int? durationSeconds, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(sheet.FragmentUrls[0], ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
                return null;

            var luma = ToLuma(bitmap);
            return MeasureFrames(
                luma, bitmap.Width, bitmap.Height,
                sheet.Rows, sheet.Columns, sheet.TileWidth, sheet.TileHeight,
                UsableTiles(sheet, durationSeconds));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Storyboard fetch failed");
            return null;
        }
    }

    private static byte[] ToLuma(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var luma = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            // Rec. 601 luma, integer-scaled — the absolute scale is irrelevant, only frame-to-frame
            // differences are compared, but keeping it in 0-255 makes the thresholds readable.
            luma[i] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
        }
        return luma;
    }

    /// <summary>
    /// How many tiles of the grid hold real frames. YouTube sizes the top storyboard level so
    /// <c>duration * fps</c> exactly fills one sheet, but a lower level's final sheet is padded —
    /// counting padding as frames would read as a long motionless stretch.
    /// </summary>
    internal static int UsableTiles(Storyboard sheet, int? durationSeconds)
    {
        var capacity = sheet.Rows * sheet.Columns;
        if (sheet.Fps is not > 0 || durationSeconds is not > 0)
            return capacity;
        var total = (int)Math.Ceiling(durationSeconds.Value * sheet.Fps.Value);
        return Math.Clamp(total, 2, capacity);
    }

    /// <summary>
    /// Mean absolute luma difference between consecutive frames of the sheet, reduced to a median
    /// and a maximum. Null when the decoded image does not match the declared grid.
    /// </summary>
    internal static (double Median, double Max)? MeasureFrames(
        byte[] luma, int sheetWidth, int sheetHeight,
        int rows, int columns, int tileWidth, int tileHeight, int usableTiles)
    {
        if (rows < 1 || columns < 1 || tileWidth < 1 || tileHeight < 1 || usableTiles < 2)
            return null;
        if (luma.Length != sheetWidth * sheetHeight)
            return null;
        if (columns * tileWidth > sheetWidth || rows * tileHeight > sheetHeight)
            return null;

        // Stride keeps the cost bounded for an unexpectedly large sheet; at the tile sizes the
        // selected level actually uses (~48x27) it resolves to 1 and every pixel is compared.
        var stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)(tileWidth * tileHeight) / MaxSampledPixelsPerFrame)));

        var deltas = new List<double>(usableTiles - 1);
        double[]? previous = null;
        for (var index = 0; index < usableTiles; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var frame = SampleTile(luma, sheetWidth, row * tileHeight, column * tileWidth, tileWidth, tileHeight, stride);
            if (previous is not null)
            {
                var sum = 0.0;
                for (var i = 0; i < frame.Length; i++)
                    sum += Math.Abs(frame[i] - previous[i]);
                deltas.Add(sum / frame.Length);
            }
            previous = frame;
        }

        if (deltas.Count == 0)
            return null;
        deltas.Sort();
        return (deltas[deltas.Count / 2], deltas[^1]);
    }

    private static double[] SampleTile(
        byte[] luma, int sheetWidth, int top, int left, int tileWidth, int tileHeight, int stride)
    {
        var samples = new List<double>((tileWidth / stride + 1) * (tileHeight / stride + 1));
        for (var y = 0; y < tileHeight; y += stride)
        {
            var rowStart = (top + y) * sheetWidth + left;
            for (var x = 0; x < tileWidth; x += stride)
                samples.Add(luma[rowStart + x]);
        }
        return [.. samples];
    }

    internal static MusicVideoMotion Classify(double medianDelta, double maxDelta)
    {
        if (medianDelta < StaticMedianDelta && maxDelta < StaticMaxDelta)
            return MusicVideoMotion.Static;
        return medianDelta >= RealVideoMedianDelta ? MusicVideoMotion.RealVideo : MusicVideoMotion.LowMotion;
    }

    internal sealed record Storyboard(
        string FormatId, int TileWidth, int TileHeight, int Rows, int Columns, double? Fps, List<string> FragmentUrls);

    internal sealed record ProbeMetadata(
        string Title, string Channel, int? DurationSeconds, long? EstimatedBytes,
        string? ThumbnailUrl, List<Storyboard> Storyboards);

    /// <summary>
    /// The sheet to measure: fewest fragments first, so a single request spans the whole runtime
    /// rather than only the opening seconds. Ties break toward the sheet holding the most frames.
    /// </summary>
    internal static Storyboard? SelectMotionSheet(List<Storyboard> storyboards) =>
        storyboards
            .Where(s => s.FragmentUrls.Count > 0 && s.Rows * s.Columns >= 2)
            .OrderBy(s => s.FragmentUrls.Count)
            .ThenByDescending(s => s.Rows * s.Columns)
            .FirstOrDefault();

    /// <summary>
    /// Whether the upload's own frame is square — a cover image filling the screen. Read from the
    /// highest-resolution storyboard: YouTube letterboxes its smallest levels into 16:9, so only the
    /// large sheets preserve the source shape.
    /// </summary>
    internal static bool IsSquareSource(List<Storyboard> storyboards)
    {
        var widest = storyboards.OrderByDescending(s => s.TileWidth).FirstOrDefault();
        if (widest is null || widest.TileHeight < 1)
            return false;
        return Math.Abs((double)widest.TileWidth / widest.TileHeight - 1.0) < 0.05;
    }

    internal static ProbeMetadata? ParseMetadata(string json, int maxHeight)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var title = StringOrEmpty(root, "title");
            var channel = root.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String
                ? ch.GetString() ?? ""
                : StringOrEmpty(root, "uploader");
            int? duration = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(dur.GetDouble())
                : null;

            var storyboards = new List<Storyboard>();
            long? estimated = null;
            if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                storyboards = ParseStoryboards(formats);
                estimated = EstimateDownloadBytes(formats, maxHeight, duration);
            }

            return new ProbeMetadata(title, channel, duration, estimated, ThumbnailUrl(root), storyboards);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StringOrEmpty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? ThumbnailUrl(JsonElement root)
    {
        if (root.TryGetProperty("thumbnail", out var single) && single.ValueKind == JsonValueKind.String)
            return single.GetString();
        if (!root.TryGetProperty("thumbnails", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        string? best = null;
        var bestWidth = -1;
        foreach (var thumb in list.EnumerateArray())
        {
            if (thumb.ValueKind != JsonValueKind.Object) continue;
            var url = thumb.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (url is null) continue;
            var width = thumb.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
            if (width > bestWidth) { best = url; bestWidth = width; }
        }
        return best;
    }

    internal static List<Storyboard> ParseStoryboards(JsonElement formats)
    {
        var result = new List<Storyboard>();
        foreach (var format in formats.EnumerateArray())
        {
            if (format.ValueKind != JsonValueKind.Object) continue;
            var id = format.TryGetProperty("format_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "" : "";
            if (!id.StartsWith("sb", StringComparison.Ordinal)) continue;

            var rows = IntOrZero(format, "rows");
            var columns = IntOrZero(format, "columns");
            var width = IntOrZero(format, "width");
            var height = IntOrZero(format, "height");
            if (rows < 1 || columns < 1 || width < 1 || height < 1) continue;

            double? fps = format.TryGetProperty("fps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number
                ? fpsEl.GetDouble() : null;

            var fragments = new List<string>();
            if (format.TryGetProperty("fragments", out var frags) && frags.ValueKind == JsonValueKind.Array)
            {
                foreach (var fragment in frags.EnumerateArray())
                {
                    if (fragment.ValueKind != JsonValueKind.Object) continue;
                    if (fragment.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(u.GetString()))
                        fragments.Add(u.GetString()!);
                }
            }
            if (fragments.Count == 0) continue;

            result.Add(new Storyboard(id, width, height, rows, columns, fps, fragments));
        }
        return result;
    }

    /// <summary>
    /// Size the real fetch would write: the best mp4 video track within the configured height cap
    /// plus the best m4a audio track, mirroring <see cref="MusicVideoDownloader.BuildFormat"/>.
    /// Falls back to bitrate x duration when yt-dlp reports no size for a format.
    /// </summary>
    internal static long? EstimateDownloadBytes(JsonElement formats, int maxHeight, int? durationSeconds)
    {
        long? bestVideo = null;
        var bestVideoHeight = -1;
        long? bestAudio = null;

        foreach (var format in formats.EnumerateArray())
        {
            if (format.ValueKind != JsonValueKind.Object) continue;
            var vcodec = StringOrEmpty(format, "vcodec");
            var acodec = StringOrEmpty(format, "acodec");
            var ext = StringOrEmpty(format, "ext");
            var hasVideo = vcodec.Length > 0 && vcodec != "none";
            var hasAudio = acodec.Length > 0 && acodec != "none";

            if (hasVideo && ext == "mp4")
            {
                var height = IntOrZero(format, "height");
                if (height < 1 || height > maxHeight || height <= bestVideoHeight) continue;
                var size = FormatBytes(format, durationSeconds, "vbr");
                if (size is null) continue;
                bestVideoHeight = height;
                bestVideo = size;
            }
            else if (!hasVideo && hasAudio && ext == "m4a")
            {
                var size = FormatBytes(format, durationSeconds, "abr");
                if (size is not null && (bestAudio is null || size > bestAudio))
                    bestAudio = size;
            }
        }

        if (bestVideo is null)
            return null;
        return bestVideo + (bestAudio ?? 0);
    }

    private static long? FormatBytes(JsonElement format, int? durationSeconds, string bitrateProperty)
    {
        if (format.TryGetProperty("filesize", out var size) && size.ValueKind == JsonValueKind.Number)
            return size.GetInt64();
        if (format.TryGetProperty("filesize_approx", out var approx) && approx.ValueKind == JsonValueKind.Number)
            return approx.GetInt64();
        if (durationSeconds is > 0
            && format.TryGetProperty(bitrateProperty, out var rate) && rate.ValueKind == JsonValueKind.Number)
        {
            // yt-dlp reports bitrates in kbit/s.
            return (long)(rate.GetDouble() * 1000 / 8 * durationSeconds.Value);
        }
        return null;
    }

    private static int IntOrZero(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(value.GetDouble())
            : 0;

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
