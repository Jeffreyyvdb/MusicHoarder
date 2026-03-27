using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Spotify;

public sealed class SpotifyCatalogSearchService(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<MusicEnricherOptions> options,
    ILogger<SpotifyCatalogSearchService> logger) : ISpotifyCatalogSearchService
{
    private const string AccountsTokenUrl = "https://accounts.spotify.com/api/token";
    private const string ApiSearchUrl = "https://api.spotify.com/v1/search";
    private static readonly TimeSpan RateLimitDefaultDelay = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 3;

    private static readonly object RateLimiterLock = new();
    private static TokenBucketRateLimiter? _sharedRateLimiter;
    private static int _sharedRate = -1;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tokenLocks = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracksAsync(
        string clientId,
        string clientSecret,
        string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return [];

        var opts = options.Value;
        var limit = Math.Clamp(opts.SpotifyApiSearchLimit, 1, 50);
        var market = string.IsNullOrWhiteSpace(opts.SpotifyApiMarket) ? null : opts.SpotifyApiMarket.Trim();

        var cacheKey = BuildSearchCacheKey(query, limit, market);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<SpotifyCatalogTrack>? cached) && cached is not null)
            return cached;

        var limiter = GetRateLimiter(opts.SpotifyApiRequestsPerSecond);
        using var lease = await limiter.AcquireAsync(permitCount: 1, ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("Spotify catalog rate limiter rejected search request");
            return [];
        }

        var accessToken = await GetAccessTokenAsync(clientId, clientSecret, ct);
        if (accessToken is null)
            return [];

        var q = Uri.EscapeDataString(query);
        var url = $"{ApiSearchUrl}?q={q}&type=track&limit={limit}";
        if (market is not null)
            url += $"&market={Uri.EscapeDataString(market)}";

        var refreshedTokenAfter401 = false;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)");

            var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? RateLimitDefaultDelay;
                logger.LogWarning(
                    "Spotify search rate limited. Retrying after {Delay}s (attempt {Attempt}/{Max})",
                    retryAfter.TotalSeconds, attempt + 1, MaxRetries);
                if (attempt < MaxRetries)
                {
                    await Task.Delay(retryAfter, ct);
                    continue;
                }

                return [];
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedTokenAfter401)
            {
                InvalidateTokenCache(clientId);
                accessToken = await GetAccessTokenAsync(clientId, clientSecret, ct);
                refreshedTokenAfter401 = true;
                if (accessToken is null)
                    return [];
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                logger.LogWarning("Spotify search unauthorized after token refresh");
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Spotify search failed: {Status} {Body}", (int)response.StatusCode, body);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tracks = ParseSearchResponse(json);
            cache.Set(
                cacheKey,
                tracks,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, opts.SpotifyApiSearchCacheMinutes))
                });
            return tracks;
        }

        return [];
    }

    private static string BuildSearchCacheKey(string query, int limit, string? market)
    {
        var raw = $"{limit}|{market ?? ""}|{query}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"spotify_catalog_search:{hash}";
    }

    private async Task<string?> GetAccessTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var cacheKey = $"spotify_cc_token:{clientId}";
        if (cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var gate = _tokenLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached) && !string.IsNullOrEmpty(cached))
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Post, AccountsTokenUrl);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            });
            request.Headers.TryAddWithoutValidation("User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)");

            var response = await httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Spotify client-credentials token request failed: {Status} {Body}",
                    (int)response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var accessToken = root.GetProperty("access_token").GetString();
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            if (string.IsNullOrEmpty(accessToken))
                return null;

            var ttl = TimeSpan.FromSeconds(Math.Max(120, expiresIn - 90));
            cache.Set(cacheKey, accessToken, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private void InvalidateTokenCache(string clientId) =>
        cache.Remove($"spotify_cc_token:{clientId}");

    private static TokenBucketRateLimiter GetRateLimiter(int requestsPerSecond)
    {
        lock (RateLimiterLock)
        {
            if (_sharedRateLimiter is not null && _sharedRate == requestsPerSecond)
                return _sharedRateLimiter;

            _sharedRateLimiter?.Dispose();
            _sharedRate = requestsPerSecond;
            _sharedRateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = requestsPerSecond,
                TokensPerPeriod = requestsPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = true,
                QueueLimit = 0
            });
            return _sharedRateLimiter;
        }
    }

    private static IReadOnlyList<SpotifyCatalogTrack> ParseSearchResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracksEl))
                return [];

            if (!tracksEl.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<SpotifyCatalogTrack>();
            foreach (var track in items.EnumerateArray())
            {
                var parsed = ParseTrack(track);
                if (parsed is not null)
                    list.Add(parsed);
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SpotifyCatalogTrack? ParseTrack(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object)
            return null;

        var id = track.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";
        if (string.IsNullOrEmpty(id))
            return null;

        var name = track.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? ""
            : "";

        var durationMs = track.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0;

        var artist = "";
        if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in artists.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                    names.Add(an.GetString()!);
            }

            artist = string.Join(", ", names);
        }

        var albumName = "";
        int? releaseYear = null;
        if (track.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            if (album.TryGetProperty("name", out var alName) && alName.ValueKind == JsonValueKind.String)
                albumName = alName.GetString() ?? "";

            if (album.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String)
                releaseYear = ParseReleaseYear(rd.GetString());
        }

        int? trackNumber = null;
        if (track.TryGetProperty("track_number", out var tn) && tn.ValueKind == JsonValueKind.Number)
            trackNumber = tn.GetInt32();

        string? isrc = null;
        if (track.TryGetProperty("external_ids", out var ext) && ext.ValueKind == JsonValueKind.Object &&
            ext.TryGetProperty("isrc", out var isrcProp) && isrcProp.ValueKind == JsonValueKind.String)
            isrc = isrcProp.GetString();

        return new SpotifyCatalogTrack(id, name, artist, albumName, releaseYear, trackNumber, durationMs, isrc);
    }

    private static int? ParseReleaseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
            return null;
        var part = releaseDate.Length >= 4 ? releaseDate[..4] : releaseDate;
        return int.TryParse(part, out var y) && y is > 1000 and < 3000 ? y : null;
    }
}
