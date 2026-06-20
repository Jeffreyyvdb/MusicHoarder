using System.Diagnostics;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Downloads a track's audio from YouTube via yt-dlp, keeping YouTube's native Opus where possible
/// (no lossy-to-lossy re-encode). Writes into the configured download staging directory.
/// <para>
/// It deliberately does NOT embed yt-dlp's metadata/thumbnail: the YouTube uploader channel lands in
/// ARTIST and the full video title in TITLE, which poisons enrichment matching. The caller
/// (<see cref="WishlistDownloadProcessor"/>) stamps the authoritative Spotify identity via
/// <see cref="DownloadTagWriter"/> instead, and album art is supplied later by the destination
/// cover-fetch pipeline.
/// </para>
/// Degrades gracefully (returns a failure result) when the yt-dlp binary is missing, mirroring the
/// fpcalc-absent handling.
/// </summary>
public class YtDlpDownloadProvider(
    IOptions<MusicEnricherOptions> options,
    ILogger<YtDlpDownloadProvider> logger) : IDownloadProvider
{
    public string Name => "yt-dlp";

    public async Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
    {
        var opts = options.Value;
        var format = string.IsNullOrWhiteSpace(opts.DownloadAudioFormat) ? "opus" : opts.DownloadAudioFormat.Trim();

        try
        {
            Directory.CreateDirectory(req.DestinationDirectory);

            // Unique stem so concurrent downloads never collide and we can locate the produced file
            // deterministically (yt-dlp writes <stem>.<format> after audio extraction).
            var stem = Guid.NewGuid().ToString("N");
            var outputTemplate = Path.Combine(req.DestinationDirectory, stem + ".%(ext)s");

            var query = BuildSearchQuery(req);

            var psi = new ProcessStartInfo(opts.YtDlpPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-progress");
            // Prefer a native opus/webm stream so extraction can copy rather than re-encode.
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("bestaudio[ext=webm]/bestaudio");
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("--audio-format");
            psi.ArgumentList.Add(format);
            // Intentionally NOT --embed-metadata / --embed-thumbnail: yt-dlp would write the YouTube
            // uploader channel into ARTIST and the full video title into TITLE, poisoning enrichment.
            // The processor stamps the known Spotify identity via DownloadTagWriter after download.
            // Built-in throttle: a short (optionally randomized) pause before each fetch so a bulk
            // wishlist run doesn't machine-gun YouTube — rapid back-to-back requests are a strong
            // bot-detection signal. Power users can still override via YtDlpExtraArgs.
            if (opts.DownloadSleepSeconds > 0)
            {
                psi.ArgumentList.Add("--sleep-interval");
                psi.ArgumentList.Add(opts.DownloadSleepSeconds.ToString());
                if (opts.DownloadMaxSleepSeconds > opts.DownloadSleepSeconds)
                {
                    psi.ArgumentList.Add("--max-sleep-interval");
                    psi.ArgumentList.Add(opts.DownloadMaxSleepSeconds.ToString());
                }
            }
            if (!string.IsNullOrWhiteSpace(opts.FfmpegPath))
            {
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(opts.FfmpegPath);
            }
            // Authenticated cookies get past YouTube's datacenter-IP bot check ("Sign in to confirm
            // you're not a bot"). Only pass it when the file actually exists — a dangling path makes
            // yt-dlp hard-fail every download.
            if (!string.IsNullOrWhiteSpace(opts.YtDlpCookiesPath))
            {
                if (File.Exists(opts.YtDlpCookiesPath))
                {
                    psi.ArgumentList.Add("--cookies");
                    psi.ArgumentList.Add(opts.YtDlpCookiesPath);
                }
                else
                {
                    logger.LogWarning("YtDlpCookiesPath is set but the file does not exist: {Path}", LogSanitizer.ForLog(opts.YtDlpCookiesPath));
                }
            }
            // Power-user escape hatch: extra extractor-args / proxy flags without a code change.
            foreach (var extra in SplitArgs(opts.YtDlpExtraArgs))
                psi.ArgumentList.Add(extra);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputTemplate);
            psi.ArgumentList.Add(query);

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read both streams concurrently to avoid buffer-full deadlock.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));

            var stderr = errorTask.Result.Trim();
            var produced = LocateProducedFile(req.DestinationDirectory, stem);

            if (produced is not null)
                return DownloadResult.Ok(produced);

            // No file came out. Distinguish "nothing matched" from a real failure.
            if (process.ExitCode == 0 || LooksLikeNoResults(stderr))
            {
                logger.LogInformation("yt-dlp found no result for '{Query}': {Error}", LogSanitizer.ForLog(query), LogSanitizer.ForLog(Truncate(stderr)));
                return DownloadResult.Missing(stderr.Length == 0 ? "no results" : Truncate(stderr));
            }

            logger.LogWarning("yt-dlp exited {Code} for '{Query}': {Error}", process.ExitCode, LogSanitizer.ForLog(query), LogSanitizer.ForLog(Truncate(stderr)));
            return DownloadResult.Failed($"exited {process.ExitCode}: {Truncate(stderr)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Missing binary (Win32Exception / file-not-found) lands here — degrade gracefully.
            logger.LogWarning(ex, "yt-dlp download failed for '{Artist} - {Title}'", LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
            return DownloadResult.Failed(ex.Message);
        }
    }

    internal static string BuildSearchQuery(DownloadRequest req)
    {
        var terms = string.IsNullOrWhiteSpace(req.Artist)
            ? req.Title
            : $"{req.Artist} {req.Title}";
        return $"ytsearch1:{terms.Trim()}";
    }

    /// <summary>
    /// Finds the file yt-dlp produced for our unique stem. The audio-extracted file is
    /// <c>&lt;stem&gt;.&lt;format&gt;</c>; fall back to any <c>&lt;stem&gt;.*</c> that isn't an
    /// intermediate part file.
    /// </summary>
    internal static string? LocateProducedFile(string directory, string stem)
    {
        var matches = Directory
            .EnumerateFiles(directory, stem + ".*")
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Only a real audio output counts as success. A leftover thumbnail (.jpg/.webp/.png) means
        // audio extraction produced nothing — returning it would mark the item Downloaded pointing at
        // an image the scanner then ignores (a silent failure). Return null so the caller reports
        // Missing/Failed and the item can be retried instead.
        return matches.FirstOrDefault(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            return ext is not (".jpg" or ".jpeg" or ".png" or ".webp");
        });
    }

    /// <summary>
    /// Splits a configured extra-args string into individual tokens on whitespace. Quoted segments
    /// ("...") are kept whole so a value containing spaces survives. Good enough for the handful of
    /// flags this is meant to carry; anything more exotic should be a first-class option.
    /// </summary>
    internal static IEnumerable<string> SplitArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in raw)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            }
            else
                current.Append(c);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    internal static bool LooksLikeNoResults(string stderr) =>
        stderr.Contains("no results", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Unable to download webpage", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("no video", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
