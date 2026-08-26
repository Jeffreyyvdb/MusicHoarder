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

    /// <summary>
    /// Runs yt-dlp to completion. On cancellation the process is KILLED rather than abandoned:
    /// awaiting with a token only stops us waiting, it does not stop yt-dlp, and an abandoned
    /// extraction keeps a YouTube connection and a CPU share for as long as it likes. Request-scoped
    /// callers cancel routinely (a client hangs up, a gateway times out), so without this every
    /// failure leaks a process and the next call is slower than the last.
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();
        try
        {
            // Read both streams concurrently to avoid buffer-full deadlock.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
            return (process.ExitCode, outputTask.Result, errorTask.Result.Trim());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>
    /// <see cref="RunAsync(ProcessStartInfo, CancellationToken)"/> with its own deadline. Returns
    /// <c>TimedOut</c> instead of throwing when the budget runs out, so a caller can degrade to "no
    /// answer" while a genuine caller-side cancellation still propagates.
    /// </summary>
    public static async Task<(bool TimedOut, int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        try
        {
            var (exitCode, stdout, stderr) = await RunAsync(psi, budget.Token);
            return (false, exitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (true, -1, string.Empty, $"timed out after {timeout.TotalSeconds:F0}s");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill — nothing useful left to do.
        }
    }
}
