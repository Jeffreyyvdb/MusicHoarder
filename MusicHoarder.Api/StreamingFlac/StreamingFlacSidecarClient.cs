using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.StreamingFlac;

/// <summary>Outcome kind the sidecar reports for an <c>/acquire</c> call.</summary>
public enum AcquireStatus
{
    /// <summary>A lossless file was written to the shared staging dir.</summary>
    Ok,

    /// <summary>No lossless source exists upstream — the caller should fall through to the next provider.</summary>
    NotFound,

    /// <summary>Sidecar/transport failure (unreachable, timeout, community-server 5xx) — a transient error.</summary>
    Error,
}

/// <summary>Result of an <c>/acquire</c> call. <see cref="File"/> is set only when <see cref="Status"/> is Ok.</summary>
public record AcquireResult(AcquireStatus Status, string? File, string? Provider, string? Error)
{
    public static AcquireResult Ok(string file, string? provider) => new(AcquireStatus.Ok, file, provider, null);
    public static AcquireResult NotFound(string? error) => new(AcquireStatus.NotFound, null, null, error);
    public static AcquireResult Errored(string? error) => new(AcquireStatus.Error, null, null, error);
}

/// <summary>
/// HTTP client for the optional streaming-FLAC acquisition sidecar. Deliberately dumb: it knows only
/// the sidecar's two-endpoint contract and treats it as an opaque service, so nothing streaming-service
/// specific lives in MusicHoarder. Unconfigured or unreachable ⇒ the caller degrades gracefully.
/// </summary>
public sealed class StreamingFlacSidecarClient(
    HttpClient httpClient,
    IOptionsMonitor<StreamingFlacOptions> options,
    ILogger<StreamingFlacSidecarClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Readiness probe: true when the sidecar answers <c>GET /health</c> 2xx with at least one working
    /// lossless provider. Used to skip work when no upstream source is up. Any failure ⇒ false.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(opts.SidecarUrl, "/health"));
            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            // A sidecar that is up but has no working lossless provider can't fulfil anything.
            if (root.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Array)
                return providers.GetArrayLength() > 0;

            return root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "streaming-flac sidecar health check failed");
            return false;
        }
    }

    /// <summary>
    /// Asks the sidecar to acquire <paramref name="spotifyUrl"/> as a single lossless file named
    /// <c>{filenameStem}.flac</c> inside <paramref name="outputDir"/> (the shared staging dir, mounted
    /// into the sidecar at the same absolute path). Maps a downed/timing-out sidecar to
    /// <see cref="AcquireStatus.Error"/> and "no lossless source" to <see cref="AcquireStatus.NotFound"/>.
    /// </summary>
    public async Task<AcquireResult> AcquireAsync(string spotifyUrl, string outputDir, string filenameStem, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            return AcquireResult.NotFound("streaming-flac sidecar not configured");

        var timeout = Math.Clamp(opts.TimeoutSeconds, 10, 600);
        var payload = JsonSerializer.Serialize(new
        {
            spotify_url = spotifyUrl,
            quality = string.IsNullOrWhiteSpace(opts.Quality) ? "LOSSLESS" : opts.Quality,
            services = opts.Services is { Length: > 0 } ? opts.Services : ["qobuz", "tidal"],
            output_dir = outputDir,
            filename_stem = filenameStem,
            timeout_s = timeout,
        }, JsonOpts);

        // Give the sidecar's own timeout a chance to fire first (returning a clean not_found/error)
        // before the client aborts the request as a transport failure.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout + 30));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(opts.SidecarUrl, "/acquire"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await httpClient.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("streaming-flac sidecar /acquire returned {Status}: {Body}",
                    (int)response.StatusCode, LogSanitizer.ForLog(Truncate(body)));
                return AcquireResult.Errored($"sidecar returned {(int)response.StatusCode}");
            }

            return ParseAcquire(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The whole download run was cancelled — propagate, don't swallow as a sidecar error.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout fired: the sidecar didn't answer in time. Transient.
            logger.LogWarning("streaming-flac sidecar /acquire timed out after {Timeout}s", timeout + 30);
            return AcquireResult.Errored("sidecar timed out");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "streaming-flac sidecar /acquire failed");
            return AcquireResult.Errored(ex.Message);
        }
    }

    private static AcquireResult ParseAcquire(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return AcquireResult.Errored("malformed sidecar response");

            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;

            switch (status?.ToLowerInvariant())
            {
                case "ok":
                    var file = root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String
                        ? f.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(file))
                        return AcquireResult.Errored("sidecar reported ok without a file path");
                    var provider = root.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()
                        : null;
                    return AcquireResult.Ok(file!, provider);

                case "not_found":
                    return AcquireResult.NotFound(error ?? "no lossless source");

                default:
                    return AcquireResult.Errored(error ?? "sidecar error");
            }
        }
        catch (JsonException)
        {
            return AcquireResult.Errored("malformed sidecar response");
        }
    }

    private static string BuildUrl(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
