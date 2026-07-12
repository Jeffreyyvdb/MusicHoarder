using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Sync;

/// <summary>Push-side HTTP client for the remote instance's <c>/api/sync</c> endpoints.</summary>
public interface ISyncPushClient
{
    Task<SyncCheckResponse?> CheckAsync(SyncCheckRequest request, CancellationToken ct);

    /// <summary>Uploads the file at <paramref name="filePath"/> with its metadata payload.</summary>
    Task<SyncUploadResponse?> UploadAsync(SyncTrackPayload payload, string filePath, CancellationToken ct);

    /// <summary>Pushes a like-only change (no file) for a track already present on the remote.</summary>
    Task<SyncLikeResponse?> PushLikeAsync(SyncLikeRequest request, CancellationToken ct);
}

/// <summary>
/// Streams multipart uploads straight off disk (no buffering a 100 MB FLAC in memory). Any
/// transport failure or non-2xx surfaces as an exception so the worker's retry/backoff owns the
/// policy — the client stays dumb.
/// </summary>
public sealed class SyncPushClient(
    HttpClient httpClient,
    IOptionsMonitor<SyncOptions> options,
    ILogger<SyncPushClient> logger) : ISyncPushClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<SyncCheckResponse?> CheckAsync(SyncCheckRequest request, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.CheckTimeoutSeconds));

        using var message = NewRequest(HttpMethod.Post, "/api/sync/check", opts);
        message.Content = new StringContent(JsonSerializer.Serialize(request, JsonOpts), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(message, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncCheckResponse>(JsonOpts, timeout.Token);
    }

    public async Task<SyncUploadResponse?> UploadAsync(SyncTrackPayload payload, string filePath, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.UploadTimeoutSeconds));

        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"), "metadata");
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", payload.FileName);

        using var message = NewRequest(HttpMethod.Post, "/api/sync/upload", opts);
        message.Content = content;

        logger.LogDebug("Uploading {File} ({Bytes} bytes) to {Remote}", payload.FileName, payload.FileSizeBytes, opts.RemoteBaseUrl);
        using var response = await httpClient.SendAsync(message, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncUploadResponse>(JsonOpts, timeout.Token);
    }

    public async Task<SyncLikeResponse?> PushLikeAsync(SyncLikeRequest request, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.CheckTimeoutSeconds));

        using var message = NewRequest(HttpMethod.Post, "/api/sync/like", opts);
        message.Content = new StringContent(JsonSerializer.Serialize(request, JsonOpts), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(message, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncLikeResponse>(JsonOpts, timeout.Token);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, SyncOptions opts)
    {
        var request = new HttpRequestMessage(method, opts.RemoteBaseUrl.TrimEnd('/') + path);
        request.Headers.Add(SyncApiKeyFilter.HeaderName, opts.ApiKey);
        return request;
    }
}
