using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Navidrome;

/// <summary>
/// Talks to a Navidrome server over its Subsonic API for the like/favorite two-way sync. Auth follows
/// the Subsonic token scheme — a per-request random salt plus <c>md5(password + salt)</c>, so the
/// password never travels in the clear. All calls return 200 even on failure with a
/// <c>{"subsonic-response":{"status":"failed","error":{...}}}</c> envelope, surfaced as
/// <see cref="NavidromeApiException"/>.
/// </summary>
public interface INavidromeClient
{
    /// <summary>Authenticated health check — false on any transport/auth failure (never throws).</summary>
    Task<bool> PingAsync(CancellationToken ct);

    /// <summary>Every currently-starred song (Subsonic <c>getStarred2</c>).</summary>
    Task<IReadOnlyList<NavidromeSong>> GetStarredSongsAsync(CancellationToken ct);

    /// <summary>Song search (Subsonic <c>search3</c>), used to resolve a MusicHoarder song's Navidrome id.</summary>
    Task<IReadOnlyList<NavidromeSong>> SearchSongsAsync(string query, int limit, CancellationToken ct);

    Task StarAsync(string songId, CancellationToken ct);
    Task UnstarAsync(string songId, CancellationToken ct);
}

public sealed class NavidromeSubsonicClient(
    HttpClient httpClient,
    IOptionsMonitor<NavidromeOptions> options,
    ILogger<NavidromeSubsonicClient> logger) : INavidromeClient
{
    // Navidrome implements Subsonic 1.16.1; the version we claim only gates response shape, not features.
    private const string ApiVersion = "1.16.1";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync("ping", query: null, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Navidrome ping failed");
            return false;
        }
    }

    public async Task<IReadOnlyList<NavidromeSong>> GetStarredSongsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync("getStarred2", query: null, ct);
        var root = doc.RootElement.GetProperty("subsonic-response");
        if (!root.TryGetProperty("starred2", out var starred))
            return [];
        return ParseSongs(starred, "song");
    }

    public async Task<IReadOnlyList<NavidromeSong>> SearchSongsAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Ask only for songs — artistCount/albumCount 0 keeps the payload small.
        var qs = $"query={Uri.EscapeDataString(query)}&songCount={limit}&artistCount=0&albumCount=0";
        using var doc = await SendAsync("search3", qs, ct);
        var root = doc.RootElement.GetProperty("subsonic-response");
        if (!root.TryGetProperty("searchResult3", out var result))
            return [];
        return ParseSongs(result, "song");
    }

    public Task StarAsync(string songId, CancellationToken ct) => StarUnstarAsync("star", songId, ct);

    public Task UnstarAsync(string songId, CancellationToken ct) => StarUnstarAsync("unstar", songId, ct);

    private async Task StarUnstarAsync(string method, string songId, CancellationToken ct)
    {
        using var _ = await SendAsync(method, $"id={Uri.EscapeDataString(songId)}", ct);
    }

    private async Task<JsonDocument> SendAsync(string method, string? query, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var url = BuildUrl(opts, method, query);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(opts.RequestTimeoutSeconds));

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        var root = doc.RootElement.GetProperty("subsonic-response");
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (!string.Equals(status, "ok", StringComparison.Ordinal))
        {
            var code = root.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var msg = root.TryGetProperty("error", out var err2) && err2.TryGetProperty("message", out var m)
                ? m.GetString() ?? "unknown" : "unknown";
            doc.Dispose();
            throw new NavidromeApiException(code, msg);
        }

        return doc;
    }

    private static string BuildUrl(NavidromeOptions opts, string method, string? query)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var token = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(opts.Password + salt)));

        var baseUrl = opts.BaseUrl.TrimEnd('/');
        var auth =
            $"u={Uri.EscapeDataString(opts.Username)}&t={token}&s={salt}" +
            $"&v={ApiVersion}&c={NavidromeOptions.ClientName}&f=json";
        var url = $"{baseUrl}/rest/{method}.view?{auth}";
        return string.IsNullOrEmpty(query) ? url : $"{url}&{query}";
    }

    private static IReadOnlyList<NavidromeSong> ParseSongs(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out var songs) || songs.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<NavidromeSong>(songs.GetArrayLength());
        foreach (var el in songs.EnumerateArray())
        {
            var id = GetString(el, "id");
            if (string.IsNullOrEmpty(id))
                continue;

            result.Add(new NavidromeSong(
                Id: id,
                Title: GetString(el, "title"),
                Artist: GetString(el, "artist"),
                Album: GetString(el, "album"),
                Path: GetString(el, "path"),
                MusicBrainzId: NullIfBlank(GetString(el, "musicBrainzId")),
                DurationSeconds: GetInt(el, "duration"),
                Suffix: GetString(el, "suffix")));
        }
        return result;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
