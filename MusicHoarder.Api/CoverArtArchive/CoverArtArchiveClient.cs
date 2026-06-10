using System.Net;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.CoverArtArchive;

/// <summary>
/// Free, no-auth Cover Art Archive access (<c>coverartarchive.org</c>, run by MusicBrainz + the
/// Internet Archive). Front covers are keyed by release / release-group MBID; the API answers with a
/// 307 redirect to archive.org which <see cref="HttpClient"/> follows automatically. 404 is the
/// expected "no art registered" signal, not an error.
/// </summary>
public sealed class CoverArtArchiveClient(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<CoverArtArchiveClient> logger) : ICoverArtArchiveClient
{
    private const string BaseUrl = "https://coverartarchive.org";
    private static readonly TimeSpan RateLimitDefaultDelay = TimeSpan.FromSeconds(5);

    // The original /front can be a 50+ MB scan; front-1200 is preferred, this cap is the safety net.
    private const long MaxImageBytes = 20 * 1024 * 1024;

    private static readonly ReconfigurableRateLimiter RateLimiter = new();

    public Task<CoverArtArchiveImage?> GetReleaseFrontAsync(string releaseMbid, CancellationToken ct = default)
        => GetFrontAsync("release", releaseMbid, ct);

    public Task<CoverArtArchiveImage?> GetReleaseGroupFrontAsync(string releaseGroupMbid, CancellationToken ct = default)
        => GetFrontAsync("release-group", releaseGroupMbid, ct);

    private async Task<CoverArtArchiveImage?> GetFrontAsync(string entity, string mbid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mbid))
            return null;

        return await DownloadAsync($"{BaseUrl}/{entity}/{Uri.EscapeDataString(mbid)}/front-1200", ct)
            ?? await DownloadAsync($"{BaseUrl}/{entity}/{Uri.EscapeDataString(mbid)}/front", ct);
    }

    private async Task<CoverArtArchiveImage?> DownloadAsync(string url, CancellationToken ct)
    {
        using var lease = await RateLimiter.AcquireAsync(options.Value.CoverArtArchiveRequestsPerSecond, ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("Cover Art Archive rate limiter could not grant a permit (disposed or canceled)");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", options.Value.MusicBrainzUserAgent);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogDebug("Cover Art Archive has no image at {Url}", url);
            return null;
        }

        // CAA / the Internet Archive throttle with 429 and 503.
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta ?? RateLimitDefaultDelay;
            logger.LogWarning("Cover Art Archive rate limited ({Status}). Retry after {Delay}s",
                (int)response.StatusCode, retryAfter.TotalSeconds);
            throw new ProviderRateLimitedException(retryAfter);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Cover Art Archive request failed: {Status} {Url}", (int)response.StatusCode, url);
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxImageBytes)
        {
            logger.LogWarning("Cover Art Archive image too large ({Bytes} bytes) at {Url}",
                response.Content.Headers.ContentLength, url);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.LongLength > MaxImageBytes)
            return null;

        return new CoverArtArchiveImage(bytes, response.Content.Headers.ContentType?.MediaType);
    }
}
