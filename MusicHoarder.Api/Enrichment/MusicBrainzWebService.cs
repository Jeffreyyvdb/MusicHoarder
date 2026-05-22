using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment;

public record MusicBrainzRecording(
    string Id,
    string Title,
    string Artist,
    string? AlbumArtist,
    string? ReleaseId,
    string? ReleaseTitle,
    int? Year,
    string? Isrc,
    int? LengthMs,
    int Score = 100,
    int CandidateCount = 1);

public interface IMusicBrainzWebService
{
    Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default);
    Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default);
    Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(string artist, string title, int limit, string? album = null, CancellationToken ct = default);
}

/// <summary>
/// Thin client over the MusicBrainz web service (musicbrainz.org/ws/2). JSON, rate-limited
/// to honor the 1 request/second policy via a shared token bucket. A descriptive User-Agent
/// is required by MusicBrainz; it is set on the injected <see cref="HttpClient"/>.
/// </summary>
public sealed class MusicBrainzWebService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<MusicBrainzWebService> logger) : IMusicBrainzWebService
{
    private static readonly object RateLimiterLock = new();
    private static TokenBucketRateLimiter? _sharedRateLimiter;
    private static int _sharedRate = -1;

    public async Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default)
    {
        var dto = await GetAsync<RecordingDto>(
            $"recording/{Uri.EscapeDataString(mbid)}?inc=artist-credits+releases+isrcs&fmt=json", ct);
        return dto is null ? null : MapRecording(dto);
    }

    public async Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
    {
        var normalized = isrc.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
        var dto = await GetAsync<IsrcDto>(
            $"isrc/{Uri.EscapeDataString(normalized)}?inc=artist-credits+releases&fmt=json", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return null;

        var count = dto.Recordings.Count;
        return MapRecording(dto.Recordings[0]) with { CandidateCount = count, Isrc = normalized };
    }

    public async Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(
        string artist, string title, int limit, string? album = null, CancellationToken ct = default)
    {
        var query = $"artist:\"{EscapeLucene(artist)}\" AND recording:\"{EscapeLucene(title)}\"";
        if (!string.IsNullOrWhiteSpace(album))
            query += $" AND release:\"{EscapeLucene(album)}\"";
        var dto = await GetAsync<SearchDto>(
            $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return [];

        return dto.Recordings.Select(MapRecording).ToList();
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct) where T : class
    {
        var limiter = GetRateLimiter(Math.Max(1, options.Value.MusicBrainzRequestsPerSecond));
        using var lease = await limiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("MusicBrainz rate limiter rejected the request");
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync(relativeUrl, ct);
            if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                logger.LogWarning("MusicBrainz throttled ({Status}); retry after {Delay}s",
                    (int)response.StatusCode, retryAfter.TotalSeconds);
                throw new ProviderRateLimitedException(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("MusicBrainz HTTP {Status} for {Url}", (int)response.StatusCode, relativeUrl);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "MusicBrainz request failed for {Url}", relativeUrl);
            return null;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    private static MusicBrainzRecording MapRecording(RecordingDto r)
    {
        var artist = BuildArtistCredit(r.ArtistCredit);
        var release = r.Releases is { Count: > 0 } ? r.Releases[0] : null;
        return new MusicBrainzRecording(
            Id: r.Id,
            Title: r.Title ?? string.Empty,
            Artist: artist,
            AlbumArtist: ArtistCreditNormalizer.GetPrimaryArtist(artist),
            ReleaseId: release?.Id,
            ReleaseTitle: release?.Title,
            Year: ParseYear(release?.Date),
            Isrc: r.Isrcs is { Count: > 0 } ? r.Isrcs[0] : null,
            LengthMs: r.Length,
            Score: r.Score ?? 100,
            CandidateCount: 1);
    }

    private static string BuildArtistCredit(List<ArtistCreditDto>? credits)
    {
        if (credits is null or { Count: 0 })
            return string.Empty;

        return string.Concat(credits.Select(c => (c.Name ?? c.Artist?.Name ?? string.Empty) + (c.JoinPhrase ?? string.Empty))).Trim();
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            return null;
        return int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
    }

    private static string EscapeLucene(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static TokenBucketRateLimiter GetRateLimiter(int requestsPerSecond)
    {
        lock (RateLimiterLock)
        {
            if (_sharedRateLimiter is not null && _sharedRate == requestsPerSecond)
                return _sharedRateLimiter;

            _sharedRateLimiter?.Dispose();
            _sharedRateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = requestsPerSecond,
                TokensPerPeriod = requestsPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = int.MaxValue,
                AutoReplenishment = true,
            });
            _sharedRate = requestsPerSecond;
            return _sharedRateLimiter;
        }
    }

    // --- JSON DTOs ---

    private sealed class IsrcDto
    {
        [JsonPropertyName("recordings")]
        public List<RecordingDto>? Recordings { get; set; }
    }

    private sealed class SearchDto
    {
        [JsonPropertyName("recordings")]
        public List<RecordingDto>? Recordings { get; set; }
    }

    private sealed class RecordingDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("length")]
        public int? Length { get; set; }

        [JsonPropertyName("score")]
        public int? Score { get; set; }

        [JsonPropertyName("artist-credit")]
        public List<ArtistCreditDto>? ArtistCredit { get; set; }

        [JsonPropertyName("releases")]
        public List<ReleaseDto>? Releases { get; set; }

        [JsonPropertyName("isrcs")]
        public List<string>? Isrcs { get; set; }
    }

    private sealed class ArtistCreditDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("joinphrase")]
        public string? JoinPhrase { get; set; }

        [JsonPropertyName("artist")]
        public ArtistDto? Artist { get; set; }
    }

    private sealed class ArtistDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }
    }
}
