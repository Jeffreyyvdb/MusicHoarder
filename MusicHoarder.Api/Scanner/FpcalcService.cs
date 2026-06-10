using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Scanner;

public record FpcalcResult(int DurationSeconds, string Fingerprint);

/// <summary>
/// Result of an fpcalc invocation: a successful <see cref="FpcalcResult"/>, or a
/// <see cref="FailureReason"/> describing why fingerprinting failed for this file
/// (so the caller can surface it instead of a bare null).
/// </summary>
public readonly record struct FpcalcOutcome(FpcalcResult? Result, string? FailureReason)
{
    public static FpcalcOutcome Success(FpcalcResult result) => new(result, null);
    public static FpcalcOutcome Failure(string reason) => new(null, reason);
}

public interface IFpcalcService
{
    Task<FpcalcOutcome> GetFingerprintAsync(string filePath, CancellationToken ct = default);
}

public class FpcalcService(
    IOptions<MusicEnricherOptions> options,
    ILogger<FpcalcService> logger) : IFpcalcService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FpcalcOutcome> GetFingerprintAsync(string filePath, CancellationToken ct = default)
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
                var stderr = errorTask.Result.Trim();
                logger.LogDebug("fpcalc exited {Code} for {File}: {Error}",
                    process.ExitCode, Path.GetFileName(filePath), stderr);
                return FpcalcOutcome.Failure($"exited {process.ExitCode}: {stderr}");
            }

            var parsed = JsonSerializer.Deserialize<FpcalcOutput>(outputTask.Result, JsonOptions);
            if (parsed?.Fingerprint is null)
            {
                logger.LogDebug("fpcalc returned empty fingerprint for {File}", Path.GetFileName(filePath));
                return FpcalcOutcome.Failure("ran but produced no fingerprint (undecodable/too short)");
            }

            return FpcalcOutcome.Success(new FpcalcResult((int)parsed.Duration, parsed.Fingerprint));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("fpcalc error for {File}: {Message}", Path.GetFileName(filePath), ex.Message);
            return FpcalcOutcome.Failure(ex.Message);
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
