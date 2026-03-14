using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment;

public record AcoustIdMatch(string MusicBrainzRecordingId, string Title, string Artist, float Score);

public interface IAcoustIdService
{
    Task<AcoustIdMatch?> LookupAsync(string fingerprint, int durationSeconds, CancellationToken ct = default);
}

public sealed class AcoustIdService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<AcoustIdService> logger) : IAcoustIdService, IDisposable
{
    private readonly TokenBucketRateLimiter _rateLimiter = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 3,
        TokensPerPeriod = 3,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = int.MaxValue,
        AutoReplenishment = true,
    });

    public async Task<AcoustIdMatch?> LookupAsync(string fingerprint, int durationSeconds, CancellationToken ct = default)
    {
        var apiKey = options.Value.AcoustIdApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("AcoustID API key is not configured; skipping lookup");
            return null;
        }

        using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("AcoustID rate limiter rejected the request");
            return null;
        }

        try
        {
            var url = $"v2/lookup?client={Uri.EscapeDataString(apiKey)}" +
                      $"&duration={durationSeconds}" +
                      $"&fingerprint={Uri.EscapeDataString(fingerprint)}" +
                      $"&meta=recordings+releasegroups";

            var response = await httpClient.GetFromJsonAsync<AcoustIdResponse>(url, ct);

            if (response?.Status != "ok" || response.Results is null or { Count: 0 })
            {
                logger.LogDebug("AcoustID returned no results (status={Status})", response?.Status);
                return null;
            }

            var threshold = options.Value.AcoustIdScoreThreshold;

            var best = response.Results
                .Where(r => r.Score >= threshold && r.Recordings is { Count: > 0 })
                .OrderByDescending(r => r.Score)
                .FirstOrDefault();

            if (best is null)
            {
                logger.LogDebug("AcoustID: no result above threshold {Threshold}", threshold);
                return null;
            }

            var recording = best.Recordings![0];
            var artist = recording.Artists is { Count: > 0 }
                ? string.Join("; ", recording.Artists.Select(a => a.Name))
                : string.Empty;

            return new AcoustIdMatch(
                MusicBrainzRecordingId: recording.Id,
                Title: recording.Title ?? string.Empty,
                Artist: artist,
                Score: best.Score
            );
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "AcoustID HTTP request failed");
            return null;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AcoustID lookup failed unexpectedly");
            return null;
        }
    }

    public void Dispose() => _rateLimiter.Dispose();

    // --- JSON DTOs ---

    private sealed class AcoustIdResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("results")]
        public List<AcoustIdResult>? Results { get; set; }
    }

    private sealed class AcoustIdResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public float Score { get; set; }

        [JsonPropertyName("recordings")]
        public List<AcoustIdRecording>? Recordings { get; set; }
    }

    private sealed class AcoustIdRecording
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("artists")]
        public List<AcoustIdArtist>? Artists { get; set; }
    }

    private sealed class AcoustIdArtist
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
