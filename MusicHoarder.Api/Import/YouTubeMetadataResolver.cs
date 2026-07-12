using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Import;

/// <summary>Metadata read off a YouTube video by a yt-dlp probe (no download).</summary>
public record YouTubeProbeResult(string Title, string Artist, int DurationMs, string? ThumbnailUrl);

public interface IYouTubeMetadataResolver
{
    /// <summary>
    /// Probes a YouTube URL for a title/artist/duration/thumbnail guess so the URL-import preview can be
    /// pre-filled (the owner then edits before confirming). Returns null when yt-dlp is missing/unreadable
    /// or the video can't be resolved — the caller surfaces a "couldn't read the video" error.
    /// </summary>
    Task<YouTubeProbeResult?> ProbeAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Reads a YouTube video's metadata via <c>yt-dlp --dump-single-json --skip-download</c>. Shares the
/// download provider's yt-dlp path/cookies/extra-args so a probe succeeds wherever a download would.
/// YouTube-Music entries expose discrete <c>artist</c>/<c>track</c> fields (preferred); otherwise the
/// artist/title is guessed from the video title ("Artist - Title"), which the owner corrects in the UI.
/// </summary>
public sealed class YouTubeMetadataResolver(
    IOptions<MusicEnricherOptions> options,
    ILogger<YouTubeMetadataResolver> logger) : IYouTubeMetadataResolver
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public async Task<YouTubeProbeResult?> ProbeAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var opts = options.Value;

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
            if (!string.IsNullOrWhiteSpace(opts.YtDlpCookiesPath) && File.Exists(opts.YtDlpCookiesPath))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(opts.YtDlpCookiesPath);
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

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(outputTask.Result))
            {
                logger.LogInformation(
                    "yt-dlp probe exited {Code} for {Url}: {Error}",
                    process.ExitCode, LogSanitizer.ForLog(url), LogSanitizer.ForLog(Truncate(errorTask.Result.Trim())));
                return null;
            }

            return Parse(outputTask.Result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Missing binary, probe timeout, or malformed output — degrade gracefully.
            logger.LogWarning(ex, "yt-dlp probe failed for {Url}", LogSanitizer.ForLog(url));
            return null;
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

            var thumbnail = GetString(root, "thumbnail");

            return new YouTubeProbeResult(title, artist, durationMs, thumbnail);
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

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
