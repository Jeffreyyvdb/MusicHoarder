using System.Diagnostics;
using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Cancelling an await does not stop a child process. These cover the part that bites in
/// production: a request-scoped caller giving up (a client hangs up, a gateway times out) must not
/// leave an extraction running, or every failure leaks a process and the host gets slower.
/// </summary>
public class YtDlpProcessTests
{
    /// <summary>A stand-in for a yt-dlp that never returns.</summary>
    private static ProcessStartInfo SleepForever() =>
        new("/bin/sh")
        {
            ArgumentList = { "-c", "sleep 300" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    [Fact]
    public async Task RunAsync_Timeout_ReportsItInsteadOfThrowing()
    {
        var (timedOut, _, stdout, stderr) = await YtDlpProcess.RunAsync(
            SleepForever(), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(timedOut);
        Assert.Empty(stdout);
        Assert.Contains("timed out", stderr);
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsTheProcess()
    {
        // Observe the pid through a marker file the child writes only if it survives the deadline.
        var marker = Path.Combine(Path.GetTempPath(), $"ytdlp-kill-{Guid.NewGuid():N}");
        var psi = new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"sleep 1; touch '{marker}'" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var (timedOut, _, _, _) = await YtDlpProcess.RunAsync(
            psi, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.True(timedOut);

        // Well past when the abandoned child would have written it.
        await Task.Delay(2000);
        Assert.False(File.Exists(marker), "yt-dlp kept running after its deadline");
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_StillThrows()
    {
        // A genuine caller-side cancel must propagate — only the internal deadline is swallowed.
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => YtDlpProcess.RunAsync(SleepForever(), TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact]
    public async Task RunAsync_NormalCompletion_ReturnsOutput()
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "echo hello" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var (timedOut, exitCode, stdout, _) = await YtDlpProcess.RunAsync(
            psi, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(timedOut);
        Assert.Equal(0, exitCode);
        Assert.Equal("hello", stdout.Trim());
    }
}
