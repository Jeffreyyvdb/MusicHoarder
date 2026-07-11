using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// slskd v0 REST client. Follows the catalog-service shape (hand-built <see cref="HttpClient"/> +
/// shared static rate limiter) but builds absolute URLs per call because the base URL is user
/// config, not a constant. Searches go through the rate limiter (Soulseek etiquette); status and
/// transfer polls are LAN-local and unthrottled.
/// </summary>
public sealed class SlskdClient(
    HttpClient httpClient,
    IOptionsMonitor<SlskdOptions> options,
    ILogger<SlskdClient> logger) : ISlskdClient
{
    private static readonly ReconfigurableRateLimiter SearchRateLimiter = new();
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<SlskdSearchState?> StartSearchAsync(string searchText, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured || string.IsNullOrWhiteSpace(searchText))
            return null;

        using var lease = await SearchRateLimiter.AcquireAsync(
            Math.Max(1, opts.SearchesPerMinute), TimeSpan.FromMinutes(1), ct);
        if (!lease.IsAcquired)
            return null;

        var body = JsonSerializer.Serialize(new
        {
            searchText,
            searchTimeout = Math.Clamp(opts.SearchTimeoutSeconds, 5, 120) * 1000,
        }, JsonOpts);

        var json = await SendAsync(HttpMethod.Post, "/api/v0/searches", body, ct);
        return json is null ? null : ParseSearchState(json);
    }

    public async Task<SlskdSearchState?> GetSearchAsync(Guid searchId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"/api/v0/searches/{searchId}", null, ct);
        return json is null ? null : ParseSearchState(json);
    }

    public async Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(Guid searchId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"/api/v0/searches/{searchId}/responses", null, ct);
        if (json is null)
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var responses = new List<SlskdSearchResponse>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var username = GetString(el, "username");
                if (string.IsNullOrEmpty(username))
                    continue;

                List<SlskdFile>? files = null;
                if (el.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                {
                    files = [];
                    foreach (var f in filesEl.EnumerateArray())
                    {
                        var filename = GetString(f, "filename");
                        if (string.IsNullOrEmpty(filename))
                            continue;
                        files.Add(new SlskdFile(
                            filename,
                            GetLong(f, "size") ?? 0,
                            (int?)GetLong(f, "bitRate"),
                            (int?)GetLong(f, "length"),
                            GetString(f, "extension"),
                            GetBool(f, "isLocked")));
                    }
                }

                responses.Add(new SlskdSearchResponse(
                    username,
                    GetBool(el, "hasFreeUploadSlot"),
                    (int?)GetLong(el, "queueLength") ?? 0,
                    GetLong(el, "uploadSpeed") ?? 0,
                    files));
            }
            return responses;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse slskd search responses for {SearchId}", searchId);
            return [];
        }
    }

    public async Task<bool> EnqueueDownloadAsync(string username, string filename, long size, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new[] { new { filename, size } }, JsonOpts);
        var json = await SendAsync(
            HttpMethod.Post, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}", body, ct,
            treatBodyAsOptional: true);
        return json is not null;
    }

    public async Task<IReadOnlyList<SlskdTransfer>> GetDownloadsAsync(string username, CancellationToken ct)
    {
        var json = await SendAsync(
            HttpMethod.Get, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}", null, ct);
        if (json is null)
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            return FlattenTransfers(doc.RootElement);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse slskd downloads for {Username}", LogSanitizer.ForLog(username));
            return [];
        }
    }

    public async Task CancelDownloadAsync(string username, string transferId, bool remove, CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Delete,
            $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(transferId)}?remove={(remove ? "true" : "false")}",
            null, ct, treatBodyAsOptional: true);
    }

    public async Task<SlskdApplicationState?> GetApplicationStateAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/v0/application", null, ct);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? serverState = null;
            if (root.TryGetProperty("server", out var server))
                serverState = GetString(server, "state");
            string? version = null;
            if (root.TryGetProperty("version", out var ver))
                version = ver.ValueKind == JsonValueKind.Object ? GetString(ver, "full") : ver.GetString();
            return new SlskdApplicationState(serverState, version);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One request against the configured base URL. Returns the response body, or null when the
    /// integration is unconfigured, the request failed, or slskd answered non-2xx.
    /// <paramref name="treatBodyAsOptional"/> makes an empty 2xx body count as success ("").
    /// </summary>
    private async Task<string?> SendAsync(
        HttpMethod method, string path, string? jsonBody, CancellationToken ct, bool treatBodyAsOptional = false)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            return null;

        var url = opts.BaseUrl.TrimEnd('/') + path;
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-API-Key", opts.ApiKey);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("slskd {Method} {Path} returned {Status}: {Body}",
                    method, path, (int)response.StatusCode, LogSanitizer.ForLog(Truncate(body)));
                return null;
            }
            return string.IsNullOrEmpty(body) && treatBodyAsOptional ? "" : body;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "slskd {Method} {Path} failed", method, path);
            return null;
        }
    }

    private static SlskdSearchState? ParseSearchState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
                return null;
            return new SlskdSearchState(
                id,
                GetString(root, "state"),
                GetString(root, "searchText"),
                (int?)GetLong(root, "responseCount") ?? 0,
                (int?)GetLong(root, "fileCount") ?? 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Transfers arrive grouped user → directories → files; flatten to files.</summary>
    private static List<SlskdTransfer> FlattenTransfers(JsonElement root)
    {
        var transfers = new List<SlskdTransfer>();
        // GET for one username returns an object; the all-downloads form returns an array of them.
        var groups = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : [root];

        foreach (var group in groups)
        {
            if (group.ValueKind != JsonValueKind.Object)
                continue;
            var username = GetString(group, "username") ?? "";
            if (!group.TryGetProperty("directories", out var dirs) || dirs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var dir in dirs.EnumerateArray())
            {
                if (!dir.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var f in files.EnumerateArray())
                {
                    var id = GetString(f, "id");
                    var filename = GetString(f, "filename");
                    if (id is null || filename is null)
                        continue;
                    transfers.Add(new SlskdTransfer(
                        id,
                        GetString(f, "username") ?? username,
                        filename,
                        GetLong(f, "size") ?? 0,
                        GetString(f, "state"),
                        GetLong(f, "bytesTransferred") ?? 0));
                }
            }
        }
        return transfers;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long? GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? (long?)p.GetDouble() : null;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
