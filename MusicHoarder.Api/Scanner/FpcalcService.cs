using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Scanner;

public record FpcalcResult(int DurationSeconds, string Fingerprint);

public interface IFpcalcService
{
    Task<FpcalcResult?> GetFingerprintAsync(string filePath, CancellationToken ct = default);
}

public class FpcalcService(
    IOptions<MusicEnricherOptions> options,
    ILogger<FpcalcService> logger) : IFpcalcService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FpcalcResult?> GetFingerprintAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(options.Value.FpcalcPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-json");
            psi.ArgumentList.Add(filePath);

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read both streams concurrently to avoid buffer-full deadlock.
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));

            if (process.ExitCode != 0)
            {
                logger.LogDebug("fpcalc exited {Code} for {File}: {Error}",
                    process.ExitCode, Path.GetFileName(filePath), errorTask.Result.Trim());
                return null;
            }

            var parsed = JsonSerializer.Deserialize<FpcalcOutput>(outputTask.Result, JsonOptions);
            if (parsed?.Fingerprint is null)
            {
                logger.LogDebug("fpcalc returned empty fingerprint for {File}", Path.GetFileName(filePath));
                return null;
            }

            return new FpcalcResult((int)parsed.Duration, parsed.Fingerprint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("fpcalc error for {File}: {Message}", Path.GetFileName(filePath), ex.Message);
            return null;
        }
    }

    private sealed class FpcalcOutput
    {
        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; set; }
    }
}
