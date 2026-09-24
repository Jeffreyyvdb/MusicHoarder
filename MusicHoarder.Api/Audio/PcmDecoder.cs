using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Audio;

/// <summary>
/// Decodes audio files to the PCM a <see cref="WavStreamResult"/> carries: 48 kHz, stereo, signed
/// 16-bit little-endian, whatever the source's own rate and layout.
/// </summary>
public interface IPcmDecoder
{
    /// <summary>
    /// How many sample frames the file decodes to at <see cref="WavStreamResult.SampleRate"/>, or null
    /// when its length cannot be read. Read from the container, so for some formats it is a few frames
    /// off what the decoder produces; the stream pads or trims to it.
    /// </summary>
    Task<long?> CountFramesAsync(string path, CancellationToken ct);

    /// <summary>
    /// The decoded PCM from <paramref name="startFrame"/> to the end of the file, or null when the
    /// server is already running as many decodes as it allows. Disposing the stream stops the
    /// decoder. Throws when the decoder cannot be started.
    /// </summary>
    Stream? Open(string path, long startFrame);
}

/// <summary>
/// <see cref="IPcmDecoder"/> over ffmpeg, one process per opened range.
///
/// <para>
/// Every range a player asks for is decoded on its own, so a range starting part-way through the file
/// must come out bit-identical to the same samples of a decode from the start, or the joins between
/// ranges would click. ffmpeg's own seek does not guarantee that: an Opus decoder started at a page
/// boundary takes about half a second to converge. So decoding starts <see cref="PrerollFrames"/>
/// early and the lead-in is thrown away, after which the output matches a continuous decode exactly.
/// </para>
/// </summary>
public sealed class FfmpegPcmDecoder(IOptions<MusicEnricherOptions> options, ILogger<FfmpegPcmDecoder> logger) : IPcmDecoder
{
    /// <summary>One second of lead-in: comfortably past Opus's convergence, and cheap to decode.</summary>
    internal const long PrerollFrames = WavStreamResult.SampleRate;

    /// <summary>Frame counts by file version: a single play asks several times (one per range).</summary>
    private readonly ConcurrentDictionary<string, long> frameCounts = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim decodes = new(options.Value.StreamDecodeConcurrency, options.Value.StreamDecodeConcurrency);

    /// <summary>
    /// Asks ffprobe, since ffmpeg does the decoding: for Ogg Opus its duration less the encoder's
    /// pre-skip is exactly what ffmpeg decodes, where TagLib reads it 312 frames short.
    /// </summary>
    public async Task<long?> CountFramesAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var key = string.Join('|', path, info.Length, info.LastWriteTimeUtc.Ticks);
        if (frameCounts.TryGetValue(key, out var known))
            return known;

        var psi = new ProcessStartInfo(FfprobePath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-select_streams", "a:0",
                     "-show_entries", "stream=duration,sample_rate,initial_padding:format=duration",
                     "-of", "json", path,
                 })
            psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // exited meanwhile
                if (ct.IsCancellationRequested)
                    throw;
                logger.LogWarning("ffprobe took too long reading {Path}", LogSanitizer.ForLog(path));
                return null;
            }
            var frames = FramesFromProbe(await stdout);
            if (frames is null)
                logger.LogWarning("ffprobe could not read the length of {Path}: {Errors}",
                    LogSanitizer.ForLog(path), (await stderr).Trim());
            else
            {
                if (frameCounts.Count > 4096)
                    frameCounts.Clear();
                frameCounts[key] = frames.Value;
            }
            return frames;
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "ffprobe not found; install ffmpeg or set MusicEnricher:FfmpegPath");
            return null;
        }
    }

    /// <summary>Frames at <see cref="WavStreamResult.SampleRate"/> from ffprobe's JSON, or null.</summary>
    internal static long? FramesFromProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement stream = default;
            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
                stream = streams[0];
            var seconds = Seconds(stream, "duration")
                ?? (root.TryGetProperty("format", out var format) ? Seconds(format, "duration") : null);
            if (seconds is not > 0)
                return null;
            // Samples the encoder put in front (Opus pre-skip), which the decoder drops.
            var rate = Seconds(stream, "sample_rate");
            if (rate is > 0 && stream.ValueKind == JsonValueKind.Object
                && stream.TryGetProperty("initial_padding", out var padding) && padding.TryGetInt64(out var paddingSamples))
                seconds -= paddingSamples / rate.Value;
            return (long)Math.Round(seconds.Value * WavStreamResult.SampleRate);
        }
        catch (JsonException)
        {
            return null;
        }

        static double? Seconds(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    /// <summary>ffprobe next to the configured ffmpeg, else on PATH.</summary>
    private string FfprobePath()
    {
        var ffmpeg = options.Value.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg))
            return "ffprobe";
        var directory = Path.GetDirectoryName(ffmpeg);
        var name = Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    public Stream? Open(string path, long startFrame)
    {
        if (!decodes.Wait(0))
            return null;
        var preroll = Math.Min(startFrame, PrerollFrames);
        var ffmpeg = string.IsNullOrWhiteSpace(options.Value.FfmpegPath) ? "ffmpeg" : options.Value.FfmpegPath;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments(path, startFrame - preroll))
            psi.ArgumentList.Add(argument);

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            decodes.Release();
            throw new InvalidOperationException(
                $"ffmpeg not found ('{ffmpeg}'). Put it on PATH or set MusicEnricher:FfmpegPath.", ex);
        }
        return new DecoderStream(process, preroll * WavStreamResult.BlockAlign, path, logger, decodes);
    }

    /// <summary>ffmpeg arguments for decoding <paramref name="path"/> from <paramref name="seekFrame"/>.</summary>
    internal static IReadOnlyList<string> Arguments(string path, long seekFrame)
    {
        var args = new List<string> { "-nostdin", "-v", "error" };
        if (seekFrame > 0)
        {
            // Before -i: an input seek, which ffmpeg makes sample-accurate by decoding and discarding.
            args.Add("-ss");
            args.Add(((decimal)seekFrame / WavStreamResult.SampleRate).ToString("0.######", CultureInfo.InvariantCulture));
        }
        args.AddRange(
        [
            "-i", path,
            // The first audio stream only; embedded cover art is a picture stream.
            "-map", "0:a:0",
            "-ac", "2",
            "-ar", WavStreamResult.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le",
            "-",
        ]);
        return args;
    }

    /// <summary>ffmpeg's stdout, less the pre-roll; disposing it stops ffmpeg.</summary>
    private sealed class DecoderStream(Process process, long prerollBytes, string path, ILogger logger, SemaphoreSlim slot) : Stream
    {
        private readonly Stream output = process.StandardOutput.BaseStream;
        // Drained all along: a full stderr pipe would stall ffmpeg.
        private readonly Task<string> stderr = process.StandardError.ReadToEndAsync();
        private long toSkip = prerollBytes;
        private bool disposed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty)
                return 0;
            while (toSkip > 0)
            {
                var skipped = await output.ReadAsync(buffer[..(int)Math.Min(buffer.Length, toSkip)], ct);
                if (skipped == 0)
                    return 0;
                toSkip -= skipped;
            }
            return await output.ReadAsync(buffer, ct);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask DisposeAsync()
        {
            await StopAsync();
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                StopAsync().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        private async Task StopAsync()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                // Still running means the range was served or the listener went away: not a failure.
                var stopped = !process.HasExited;
                if (stopped)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                var errors = (await stderr).Trim();
                if (!stopped && process.ExitCode != 0)
                    logger.LogWarning("ffmpeg could not decode {Path} (exit {ExitCode}): {Errors}",
                        LogSanitizer.ForLog(path), process.ExitCode, errors);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
                slot.Release();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
