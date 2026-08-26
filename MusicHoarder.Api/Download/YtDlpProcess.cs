using System.Diagnostics;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Shared construction and execution of yt-dlp invocations for the music-video side of the
/// downloader (fetch, metadata probe, search). Extracted so <see cref="MusicVideoDownloader"/> and
/// <see cref="MusicVideoProbe"/> apply the same cookies/ffmpeg/extra-args handling — a probe that
/// authenticated differently from the download it predicts would report on a different video than
/// the one that ends up on disk.
/// </summary>
internal static class YtDlpProcess
{
    /// <summary>
    /// A yt-dlp command with the common flags applied. <paramref name="includeThrottle"/> adds the
    /// sleep intervals: they exist to pace bulk downloads and only add latency to a metadata call.
    /// </summary>
    public static ProcessStartInfo Create(MusicEnricherOptions opts, string? cookiesPath, bool includeThrottle)
    {
        var psi = new ProcessStartInfo(opts.YtDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-progress");
        if (includeThrottle && opts.DownloadSleepSeconds > 0)
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
        if (cookiesPath is not null)
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesPath);
        }
        foreach (var extra in YtDlpDownloadProvider.SplitArgs(opts.YtDlpExtraArgs))
            psi.ArgumentList.Add(extra);
        return psi;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();
        // Read both streams concurrently to avoid buffer-full deadlock.
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        return (process.ExitCode, outputTask.Result, errorTask.Result.Trim());
    }
}
