using Microsoft.Extensions.Caching.Memory;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Logging;

namespace MusicHoarder.Api.Catalog;

/// <summary>
/// Deezer's 30-second preview clips, fetched server-side because the page's CSP only allows audio from
/// its own origin. The clip URL is read fresh from Deezer each time it is needed (it carries an
/// expiring token), the bytes are held for 30 minutes.
/// </summary>
public sealed class CatalogPreviewService(
    IDeezerCatalogService deezer,
    CatalogHttpClient http,
    IMemoryCache cache,
    ILogger<CatalogPreviewService> logger)
{
    internal const int MaxClipBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ClipLifetime = TimeSpan.FromMinutes(30);

    /// <summary>The clip, or null when the track has none. Throws <see cref="HttpRequestException"/> when the CDN fails.</summary>
    public async Task<byte[]?> GetClipAsync(string deezerId, CancellationToken ct)
    {
        var key = $"catalog_preview:{deezerId}";
        if (cache.TryGetValue(key, out byte[]? cached) && cached is not null)
            return cached;

        var url = await deezer.GetPreviewUrlAsync(deezerId, ct);
        if (string.IsNullOrEmpty(url))
            return null;

        if (!CatalogHosts.IsDeezerCdn(url))
        {
            logger.LogWarning("Deezer preview for {DeezerId} points outside dzcdn.net; not fetching it", LogSanitizer.ForLog(deezerId));
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CatalogHttp.HeadersTimeout);
        var client = http.Client;
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxClipBytes)
            return Oversized(deezerId);

        await using var body = await response.Content.ReadAsStreamAsync(budget.Token);
        var bytes = await ReadCappedAsync(body, MaxClipBytes, budget.Token);
        if (bytes is null)
            return Oversized(deezerId);

        cache.Set(key, bytes, ClipLifetime);
        return bytes;
    }

    private byte[]? Oversized(string deezerId)
    {
        logger.LogWarning("Deezer preview for {DeezerId} is larger than {Cap} bytes; refusing it", LogSanitizer.ForLog(deezerId), MaxClipBytes);
        return null;
    }

    /// <summary>Reads <paramref name="source"/> to its end, or null once it passes <paramref name="cap"/> bytes.</summary>
    internal static async Task<byte[]?> ReadCappedAsync(Stream source, int cap, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
