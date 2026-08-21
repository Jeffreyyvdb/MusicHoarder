using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Import;

/// <summary>Metadata read off a YouTube video by a yt-dlp probe (no download).</summary>
public record YouTubeProbeResult(string Title, string Artist, string? Album, int DurationMs, string? ThumbnailUrl);

/// <summary>
/// Outcome of a probe: either a successful <see cref="Result"/>, or a failure carrying a human
/// <see cref="Hint"/> (e.g. "sign in required — configure cookies") and a truncated <see cref="Detail"/>
/// from yt-dlp's stderr for diagnostics. <see cref="BinaryMissing"/> flags the yt-dlp-not-installed case
/// so the caller can say so specifically instead of blaming the video.
/// </summary>
public record YouTubeProbeOutcome(YouTubeProbeResult? Result, string? Hint, string? Detail, bool BinaryMissing)
{
    public bool Ok => Result is not null;
    public static YouTubeProbeOutcome Success(YouTubeProbeResult r) => new(r, null, null, false);
    public static YouTubeProbeOutcome Failed(string? hint, string? detail) => new(null, hint, detail, false);
    public static YouTubeProbeOutcome Missing() => new(null, "yt-dlp is not installed on the server.", null, true);
}

public interface IYouTubeMetadataResolver
{
    /// <summary>
    /// Probes a YouTube URL for a title/artist/duration/thumbnail guess so the URL-import preview can be
    /// pre-filled (the owner then edits before confirming). Returns a <see cref="YouTubeProbeOutcome"/> that
    /// is either a success or a classified failure the caller can surface.
    /// </summary>
    Task<YouTubeProbeOutcome> ProbeAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Reads a YouTube video's metadata via <c>yt-dlp --dump-single-json --skip-download</c>. Shares the
/// download provider's yt-dlp path/cookies/extra-args so a probe succeeds wherever a download would.
/// YouTube-Music entries expose discrete <c>artist</c>/<c>track</c>/<c>album</c> fields (preferred);
/// otherwise the artist/title is guessed from the video title ("Artist - Title"), which the owner
/// corrects in the UI.
/// </summary>
public sealed class YouTubeMetadataResolver(
    IOptions<MusicEnricherOptions> options,
    ILogger<YouTubeMetadataResolver> logger) : IYouTubeMetadataResolver
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public async Task<YouTubeProbeOutcome> ProbeAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return YouTubeProbeOutcome.Failed(null, null);
        var opts = options.Value;

        // yt-dlp rewrites the --cookies file on exit; a read-only mount makes that write crash it (after
        // it already emitted the JSON). Give it a writable copy (cleaned up in finally).
        var cookiesPath = YtDlpCookies.PrepareWritableCopy(opts.YtDlpCookiesPath, logger);
        try
        {
            var psi = new ProcessStartInfo(opts.YtDlpPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--dump-single-json");
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-warnings");
            if (cookiesPath is not null)
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(cookiesPath);
            }
            foreach (var extra in YtDlpDownloadProvider.SplitArgs(opts.YtDlpExtraArgs))
                psi.ArgumentList.Add(extra);
            psi.ArgumentList.Add(url.Trim());

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);
            var token = timeoutCts.Token;

            using var process = new Process { StartInfo = psi };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(token));

            var stderr = errorTask.Result.Trim();

            // Trust parseable stdout regardless of exit code: yt-dlp can emit the full JSON and THEN crash
            // on exit (e.g. saving a read-only cookies file), so a non-zero exit doesn't mean no result.
            var parsed = string.IsNullOrWhiteSpace(outputTask.Result) ? null : Parse(outputTask.Result);
            if (parsed is not null)
                return YouTubeProbeOutcome.Success(parsed);

            // Surface the TAIL of stderr: yt-dlp's "ERROR:" line and a Python traceback's actual exception
            // both live at the end, so head-truncation would hide the root cause.
            logger.LogInformation(
                "yt-dlp probe exited {Code} for {Url}: {Error}",
                process.ExitCode, LogSanitizer.ForLog(url), LogSanitizer.ForLog(YtDlpErrors.Tail(stderr)));
            return YouTubeProbeOutcome.Failed(YtDlpErrors.Classify(stderr), YtDlpErrors.Tail(stderr));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own probe timeout (not the caller's token) — surface it distinctly.
            logger.LogWarning("yt-dlp probe timed out after {Seconds}s for {Url}", ProbeTimeout.TotalSeconds, LogSanitizer.ForLog(url));
            return YouTubeProbeOutcome.Failed("Timed out reading the video.", null);
        }
        catch (Win32Exception ex)
        {
            // yt-dlp binary not found / not executable.
            logger.LogWarning(ex, "yt-dlp binary unavailable for probe ({Path})", LogSanitizer.ForLog(opts.YtDlpPath));
            return YouTubeProbeOutcome.Missing();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "yt-dlp probe failed for {Url}", LogSanitizer.ForLog(url));
            return YouTubeProbeOutcome.Failed(null, ex.Message);
        }
        finally
        {
            YtDlpCookies.Cleanup(cookiesPath, opts.YtDlpCookiesPath);
        }
    }

    internal static YouTubeProbeResult? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var rawTitle = GetString(root, "title") ?? "";
            var track = GetString(root, "track");
            var artistField = GetString(root, "artist");
            var uploader = GetString(root, "uploader") ?? GetString(root, "channel");

            var (artist, title) = Derive(rawTitle, track, artistField, uploader);
            if (string.IsNullOrWhiteSpace(title)) return null;

            var durationMs = 0;
            if (root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                durationMs = (int)Math.Round(dur.GetDouble() * 1000);

            // YouTube Music / "- Topic" uploads carry the real release name here. Everything else has
            // no album at all — the import endpoint files those as a single named after the track.
            var album = GetString(root, "album")?.Trim();

            return new YouTubeProbeResult(
                title, artist, string.IsNullOrWhiteSpace(album) ? null : album, durationMs, PickThumbnail(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort artist/title split. YouTube-Music videos carry discrete <c>artist</c>/<c>track</c>
    /// fields — trust those. Otherwise fall back to a "Artist - Title" split of the video title, then to
    /// the uploader as artist (with the auto-generated " - Topic" channel suffix stripped).
    /// </summary>
    internal static (string Artist, string Title) Derive(string rawTitle, string? track, string? artist, string? uploader)
    {
        if (!string.IsNullOrWhiteSpace(track) && !string.IsNullOrWhiteSpace(artist))
            return (artist!.Trim(), track!.Trim());

        var t = rawTitle.Trim();
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var left = t[..dash].Trim();
            var right = t[(dash + 3)..].Trim();
            if (left.Length > 0 && right.Length > 0)
                return (left, right);
        }

        var uploaderArtist = (uploader ?? "").Trim();
        const string topic = " - Topic";
        if (uploaderArtist.EndsWith(topic, StringComparison.OrdinalIgnoreCase))
            uploaderArtist = uploaderArtist[..^topic.Length].Trim();
        return (uploaderArtist, t);
    }

    /// <summary>
    /// Picks the image the import preview shows and the downloader embeds as cover art. yt-dlp orders
    /// <c>thumbnails</c> worst → best, so the last entry is the largest — but prefer the last JPEG:
    /// YouTube's best variants are WebP, and WebP embedded artwork is read by far fewer players/servers
    /// than JPEG. Falls back to the flat <c>thumbnail</c> field.
    /// </summary>
    internal static string? PickThumbnail(JsonElement root)
    {
        string? lastAny = null;
        string? lastJpeg = null;

        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var thumb in thumbs.EnumerateArray())
            {
                if (thumb.ValueKind != JsonValueKind.Object) continue;
                var url = GetString(thumb, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                lastAny = url;
                // Ignore any query string — ytimg appends "?sqp=…" to the image path.
                var path = url.Split('?')[0];
                if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    lastJpeg = url;
            }
        }

        return lastJpeg ?? GetString(root, "thumbnail") ?? lastAny;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
