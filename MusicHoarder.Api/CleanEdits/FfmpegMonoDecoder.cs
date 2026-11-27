using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>Decodes an audio file to the mono 22.05 kHz float samples the spectrogram reads.</summary>
internal interface IMonoDecoder
{
    /// <summary>
    /// The samples, or null when the file cannot be decoded. Throws <see cref="FfmpegNotFoundException"/>
    /// when there is no decoder to run, which no file can fix.
    /// </summary>
    Task<float[]?> DecodeAsync(string path, CancellationToken ct);
}

internal sealed class FfmpegNotFoundException(string ffmpeg, Exception inner)
    : Exception($"ffmpeg was not found ('{ffmpeg}'). Put it on PATH or set MusicEnricher:FfmpegPath.", inner);

internal sealed class FfmpegMonoDecoder(IOptions<MusicEnricherOptions> options, ILogger<FfmpegMonoDecoder> logger) : IMonoDecoder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public async Task<float[]?> DecodeAsync(string path, CancellationToken ct)
    {
        var ffmpeg = string.IsNullOrWhiteSpace(options.Value.FfmpegPath) ? "ffmpeg" : options.Value.FfmpegPath;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments(path))
            psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new FfmpegNotFoundException(ffmpeg, ex);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);
            using var samples = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(samples, limit.Token);
            await process.WaitForExitAsync(limit.Token);
            var errors = await stderr;
            if (process.ExitCode != 0)
            {
                logger.LogDebug("ffmpeg could not decode {Path}: {Error}", path, errors);
                return null;
            }
            var bytes = samples.GetBuffer().AsSpan(0, (int)samples.Length);
            return MemoryMarshal.Cast<byte, float>(bytes[..(bytes.Length / sizeof(float) * sizeof(float))]).ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            logger.LogDebug("ffmpeg timed out decoding {Path}", path);
            return null;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    // An hour-long mix would decode to about 300 MB of samples. The first 15 minutes hold any track a
    // catalog sells two editions of.
    internal const int MaxSeconds = 900;

    internal static IReadOnlyList<string> Arguments(string path) =>
    [
        "-nostdin", "-v", "error",
        "-i", path,
        "-map", "0:a:0",
        "-t", MaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-ac", "1",
        "-ar", BandSpectrogram.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "f32le",
        "-",
    ];

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
