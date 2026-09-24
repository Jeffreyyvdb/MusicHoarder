using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Audio;

/// <summary>What a stream request asked for: the file as it is, or an AAC rendition of it.</summary>
public enum StreamFormat
{
    Original,
    Aac,
}

public static class StreamFormats
{
    /// <summary>Content type of an AAC rendition (AAC in an MP4 container, i.e. an .m4a file).</summary>
    public const string AacContentType = "audio/mp4";

    /// <summary>
    /// Parses the stream endpoints' <c>?format=</c> value. Absent means the original; anything
    /// other than a known format is rejected rather than silently served as the original, so a
    /// client asking for something this server cannot produce finds out.
    /// </summary>
    public static bool TryParse(string? value, out StreamFormat format)
    {
        format = StreamFormat.Original;
        if (string.IsNullOrEmpty(value))
            return true;
        if (string.Equals(value, "aac", StringComparison.OrdinalIgnoreCase))
        {
            format = StreamFormat.Aac;
            return true;
        }
        return false;
    }
}

public interface IStreamTranscoder
{
    /// <summary>
    /// Path of a cached AAC rendition of <paramref name="sourcePath"/>, converting it on first
    /// request. Throws when the conversion fails. <paramref name="ct"/> only stops this caller
    /// waiting: the conversion carries on while anyone else still waits for it.
    /// </summary>
    Task<string> GetAacRenditionAsync(string sourcePath, CancellationToken ct);
}

/// <summary>
/// Converts a song to AAC with ffmpeg for a client that cannot play the original, and caches the
/// result. Only the stream endpoints' <c>?format=aac</c> reaches this, and the web player only asks
/// for it when its browser cannot play the file, so no client that can play the original ever pays
/// for a conversion.
///
/// <para>
/// The whole file is converted before the first byte goes out, rather than piped as ffmpeg produces
/// it, because Safari (the client this exists for) will not play media from a server that cannot
/// answer byte-range requests, and a pipe has neither a length nor ranges. A four-minute song takes a
/// second or two, and the web player converts the next track ahead of time so a queue does not wait.
/// </para>
///
/// <para>
/// Everything that arrives for one rendition at once (the player's pre-flight, the media element's
/// own request, its range probes) shares one conversion. It is cancelled only when every one of those
/// requests has gone, so skipping quickly through a queue does not leave a backlog of conversions
/// in front of the song actually wanted.
/// </para>
/// </summary>
public sealed class FfmpegStreamTranscoder : IStreamTranscoder
{
    /// <summary>
    /// Bumped whenever the ffmpeg arguments change, so renditions made the old way are converted
    /// again instead of served from the cache.
    /// </summary>
    private const string RenditionVersion = "aac-v1";

    /// <summary>A hit refreshes its last-used stamp at most this often; range probes arrive in bursts.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>A temp file this old is left over from a crash, not a conversion still running.</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    private readonly string cacheDirectory;
    private readonly StreamTranscodeOptions options;
    private readonly Func<string, string, CancellationToken, Task> encode;
    private readonly ILogger logger;
    private readonly CancellationToken stopping;
    private readonly SemaphoreSlim slots;
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public FfmpegStreamTranscoder(
        string cacheDirectory,
        IOptions<StreamTranscodeOptions> options,
        IOptions<MusicEnricherOptions> enricherOptions,
        IHostApplicationLifetime lifetime,
        ILogger<FfmpegStreamTranscoder> logger)
        : this(
            cacheDirectory,
            options.Value,
            FfmpegEncoder(enricherOptions.Value.FfmpegPath, options.Value.AacBitrateKbps),
            logger,
            lifetime.ApplicationStopping)
    {
    }

    /// <summary>Test seam: <paramref name="encode"/> stands in for ffmpeg (source, output, ct).</summary>
    internal FfmpegStreamTranscoder(
        string cacheDirectory,
        StreamTranscodeOptions options,
        Func<string, string, CancellationToken, Task> encode,
        ILogger logger,
        CancellationToken stopping)
    {
        this.cacheDirectory = cacheDirectory;
        this.options = options;
        this.encode = encode;
        this.logger = logger;
        this.stopping = stopping;
        slots = new SemaphoreSlim(options.Concurrency, options.Concurrency);
    }

    public async Task<string> GetAacRenditionAsync(string sourcePath, CancellationToken ct)
    {
        var target = RenditionPathFor(sourcePath);
        if (File.Exists(target))
        {
            MarkUsed(target);
            return target;
        }

        Job job;
        lock (gate)
        {
            // A job whose last waiter just left is being cancelled; start afresh rather than join it.
            if (!jobs.TryGetValue(target, out job!) || job.Cancellation.IsCancellationRequested)
            {
                job = new Job(CancellationTokenSource.CreateLinkedTokenSource(stopping));
                jobs[target] = job;
                var started = job;
                job.Task = Task.Run(() => RunAsync(sourcePath, target, started), CancellationToken.None);
            }
            job.Waiters++;
        }

        try
        {
            return await job.Task.WaitAsync(ct);
        }
        finally
        {
            lock (gate)
            {
                if (--job.Waiters == 0 && !job.Finished)
                    job.Cancellation.Cancel();
            }
        }
    }

    /// <summary>
    /// The cache file for a source. Keyed on the file's path, size and last write, so a re-tagged or
    /// replaced file converts again (the library builder rewrites tags in place), and on the bitrate
    /// and <see cref="RenditionVersion"/>, so changing either does not serve stale renditions.
    /// </summary>
    internal string RenditionPathFor(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var identity = string.Join('|',
            sourcePath, info.Length, info.LastWriteTimeUtc.Ticks, options.AacBitrateKbps, RenditionVersion);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(cacheDirectory, hash[..2], $"{hash}.m4a");
    }

    private async Task<string> RunAsync(string sourcePath, string target, Job job)
    {
        var ct = job.Cancellation.Token;
        try
        {
            await slots.WaitAsync(ct);
            try
            {
                // Another job for this rendition may have finished while this one waited for a slot.
                if (File.Exists(target))
                    return target;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = $"{target}.{Guid.NewGuid():N}.tmp";
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        await encode(sourcePath, temp, deadline.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Converting took longer than {options.TimeoutSeconds} s (StreamTranscode:TimeoutSeconds).");
                    }
                    File.Move(temp, target, overwrite: true);
                    logger.LogInformation(
                        "Converted {Path} to AAC for streaming in {ElapsedMs} ms",
                        LogSanitizer.ForLog(sourcePath), clock.ElapsedMilliseconds);
                }
                finally
                {
                    TryDelete(temp); // gone already after the move; a leftover after a failure
                }
            }
            finally
            {
                slots.Release();
            }

            Prune(keep: target);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Converting {Path} to AAC for streaming failed", LogSanitizer.ForLog(sourcePath));
            throw;
        }
        finally
        {
            lock (gate)
            {
                job.Finished = true;
                if (jobs.TryGetValue(target, out var current) && ReferenceEquals(current, job))
                    jobs.Remove(target);
            }
            job.Cancellation.Dispose();
        }
    }

    /// <summary>Records a cache hit for <see cref="Prune"/>, which evicts the least recently used.</summary>
    private static void MarkUsed(string path)
    {
        // Last ACCESS, not last write: the write time is part of the file's ETag, and changing it
        // under a player that is part-way through its range requests would restart its download.
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastAccessTimeUtc(path) > TouchInterval)
                File.SetLastAccessTimeUtc(path, now);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Trims the cache back under <see cref="StreamTranscodeOptions.CacheMaxMegabytes"/>, least
    /// recently used first, and sweeps temp files left behind by a crash. Never removes
    /// <paramref name="keep"/>, the rendition about to be served.
    /// </summary>
    private void Prune(string keep)
    {
        try
        {
            var root = new DirectoryInfo(cacheDirectory);
            var now = DateTime.UtcNow;
            foreach (var temp in root.EnumerateFiles("*.tmp", SearchOption.AllDirectories))
            {
                if (now - temp.LastWriteTimeUtc > StaleTempAge)
                    TryDelete(temp.FullName);
            }

            var renditions = root.EnumerateFiles("*.m4a", SearchOption.AllDirectories).ToList();
            var total = renditions.Sum(f => f.Length);
            var cap = (long)options.CacheMaxMegabytes * 1024 * 1024;
            foreach (var file in renditions.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= cap)
                    break;
                if (string.Equals(file.FullName, keep, StringComparison.Ordinal))
                    continue;
                total -= file.Length;
                TryDelete(file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Trimming the stream transcode cache failed");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// ffmpeg arguments for one rendition: the first audio stream only, as AAC in an MP4 container
    /// with its index at the front.
    /// </summary>
    internal static IReadOnlyList<string> AacArguments(string input, string output, int bitrateKbps) =>
    [
        "-nostdin", "-v", "error", "-y",
        "-i", input,
        // Audio only. Embedded cover art is a picture stream ffmpeg would otherwise try to carry
        // over as video, and the player never shows it (the cover comes from its own endpoint).
        "-map", "0:a:0",
        "-c:a", "aac",
        "-b:a", $"{bitrateKbps}k",
        // The index (moov) at the front, so a player can start and seek without reading to the end.
        "-movflags", "+faststart",
        "-f", "mp4",
        output,
    ];

    internal static Func<string, string, CancellationToken, Task> FfmpegEncoder(string? ffmpegPath, int bitrateKbps) =>
        async (input, output, ct) =>
        {
            var ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in AacArguments(input, output, bitrateKbps))
                psi.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    $"ffmpeg not found ('{ffmpeg}'). Put it on PATH or set MusicEnricher:FfmpegPath.", ex);
            }

            // WaitForExitAsync(ct) would stop waiting but leave ffmpeg running, so a cancelled
            // conversion kills the process instead.
            await using var kill = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) { } // already exited
            });
            // Drain both pipes while waiting: a full stderr pipe would stall ffmpeg.
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(CancellationToken.None));

            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var message = stderr.Result.Trim();
                if (message.Length > 400)
                    message = message[..400];
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {message}");
            }
        };

    private sealed class Job(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<string> Task { get; set; } = null!;
        public int Waiters { get; set; }
        public bool Finished { get; set; }
    }
}
