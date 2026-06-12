using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Enrichment;

public record AcoustIdMatch(
    string MusicBrainzRecordingId,
    string AcoustIdTrackId,
    string Title,
    string Artist,
    string AlbumArtist,
    float Score,
    int? RecordingDurationMs = null,
    int CandidateCount = 1,
    /// <summary>Discrete credited artist names, ';'-joined (<see cref="Metadata.MultiValue"/>).</summary>
    string? Artists = null,
    /// <summary>Per-artist MusicBrainz ids (AcoustID artist ids ARE MBIDs), ';'-joined and
    /// positionally aligned with <see cref="Artists"/>; null when any artist lacks an id.</summary>
    string? ArtistMusicBrainzIds = null);

public interface IAcoustIdService
{
    Task<AcoustIdMatch?> LookupAsync(string fingerprint, int durationSeconds, CancellationToken ct = default);
}

public sealed class AcoustIdService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<AcoustIdService> logger) : IAcoustIdService
{
    private static readonly ReconfigurableRateLimiter RateLimiter = new();

    public async Task<AcoustIdMatch?> LookupAsync(string fingerprint, int durationSeconds, CancellationToken ct = default)
    {
        var apiKey = options.Value.AcoustIdApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("AcoustID API key is not configured; skipping lookup");
            return null;
        }

        using var lease = await RateLimiter.AcquireAsync(options.Value.AcoustIdRequestsPerSecond, ct);
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

            using var responseMessage = await httpClient.GetAsync(url, ct);
            if ((int)responseMessage.StatusCode == 429)
            {
                var retryAfter = responseMessage.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                logger.LogWarning(
                    "AcoustID returned 429 Too Many Requests. RetryAfter={RetryAfterSeconds}",
                    retryAfter.TotalSeconds);
                throw new ProviderRateLimitedException(retryAfter);
            }

            if (!responseMessage.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "AcoustID HTTP status {StatusCode} for lookup",
                    (int)responseMessage.StatusCode);
                return null;
            }

            var response = await responseMessage.Content.ReadFromJsonAsync<AcoustIdResponse>(cancellationToken: ct);

            if (response?.Status != "ok" || response.Results is null or { Count: 0 })
            {
                logger.LogDebug("AcoustID returned no results (status={Status})", response?.Status);
                return null;
            }

            const float considerationThreshold = 0.4f;

            var viable = response.Results
                .Where(r => r.Score >= considerationThreshold && r.Recordings is { Count: > 0 })
                .OrderByDescending(r => r.Score)
                .ToList();

            if (viable.Count == 0)
            {
                logger.LogDebug("AcoustID: no result above consideration threshold {Threshold}", considerationThreshold);
                return null;
            }

            var best = viable[0];
            var candidateCount = viable.Count(r => Math.Abs(r.Score - best.Score) <= 0.1f);

            var recording = best.Recordings![0];
            var displayArtist = recording.Artists is { Count: > 0 }
                ? string.Join("; ", recording.Artists.Select(a => a.Name))
                : string.Empty;
            var albumArtist = ArtistCreditNormalizer.GetPrimaryArtist(displayArtist) ?? string.Empty;
            var recordingDurationMs = recording.Duration > 0 ? (int)(recording.Duration * 1000) : (int?)null;

            // Keep the discrete artist list (and ids — AcoustID artist ids are MusicBrainz artist
            // ids) instead of flattening to the display join only; the tag writer needs per-artist
            // values for the multi-valued ARTISTS / MUSICBRAINZ_ARTISTID frames. Ids only when every
            // artist has one, so the two lists stay positionally aligned.
            var discreteArtists = MultiValue.Join(recording.Artists?.Select(a => a.Name));
            var artistMbids = recording.Artists is { Count: > 0 } all
                && all.All(a => !string.IsNullOrWhiteSpace(a.Id))
                ? MultiValue.Join(all.Select(a => a.Id))
                : null;

            return new AcoustIdMatch(
                MusicBrainzRecordingId: recording.Id,
                AcoustIdTrackId: best.Id,
                Title: recording.Title ?? string.Empty,
                Artist: displayArtist,
                AlbumArtist: albumArtist,
                Score: best.Score,
                RecordingDurationMs: recordingDurationMs,
                CandidateCount: candidateCount,
                Artists: discreteArtists,
                ArtistMusicBrainzIds: artistMbids
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

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

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
