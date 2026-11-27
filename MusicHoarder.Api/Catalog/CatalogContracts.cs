using System.Net;
using System.Text.RegularExpressions;
using MusicHoarder.Api.Deezer;

namespace MusicHoarder.Api.Catalog;

/// <summary>
/// One search hit. Carries only whether a clip exists: the clip URL holds an expiring token and is
/// served through <c>/api/catalog/preview/{deezerId}</c> instead.
/// </summary>
public sealed record CatalogTrack(
    string DeezerId,
    string Title,
    string Artist,
    string? Album,
    int DurationMs,
    string? CoverUrl,
    bool Explicit,
    bool HasPreview,
    string? WishlistStatus)
{
    internal static CatalogTrack From(DeezerCatalogTrack track, string? wishlistStatus) => new(
        track.Id,
        track.Title,
        track.Artist,
        string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName,
        track.DurationMs,
        track.CoverUrl,
        track.Explicit,
        !string.IsNullOrEmpty(track.PreviewUrl),
        wishlistStatus);
}

public sealed record CatalogSearchResponse(IReadOnlyList<CatalogTrack> Tracks);

public sealed record FullTrackResolveResponse(string YouTubeVideoId, string VideoTitle, int? DurationMs);

/// <summary>A YouTube audio stream found for a catalog track. The URL is a signed, expiring googlevideo link.</summary>
public sealed record ResolvedAudio(
    string StreamUrl,
    string ContentType,
    string YouTubeVideoId,
    string VideoTitle,
    int? DurationMs);

public enum CatalogResolveFailure
{
    /// <summary>Deezer has no such track, or YouTube has nothing usable for it.</summary>
    NotFound,
    /// <summary>yt-dlp ran and failed.</summary>
    YtDlpFailed,
    /// <summary>The yt-dlp binary is not there.</summary>
    YtDlpMissing,
}

public sealed class CatalogResolveException(CatalogResolveFailure kind, string message) : Exception(message)
{
    public CatalogResolveFailure Kind { get; } = kind;
}

internal static partial class CatalogIds
{
    // Deezer ids are 64-bit numbers; the cap keeps a route value from becoming a cache key of any size.
    [GeneratedRegex(@"^[0-9]{1,19}$")]
    private static partial Regex DigitsOnly();

    internal static bool IsValid(string? id) => id is not null && DigitsOnly().IsMatch(id);
}

internal static class CatalogHosts
{
    internal static bool IsDeezerCdn(string? url) => IsHttpsHostOrSubdomain(url, "dzcdn.net");

    internal static bool IsGoogleVideo(string? url) => IsHttpsHostOrSubdomain(url, "googlevideo.com");

    private static bool IsHttpsHostOrSubdomain(string? url, string domain)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        return uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The client for Deezer's clip CDN and googlevideo. It is built by hand rather than taken from
/// <c>IHttpClientFactory</c>, because ServiceDefaults gives every factory client a standard resilience
/// handler (a 30 s total timeout and retries) that would cut a song mid-stream and replay a request
/// after its body started. This one has no total timeout and redirects are off (a redirect could leave
/// the host allow-list); callers bound the wait for response headers with <see cref="HeadersTimeout"/>.
/// </summary>
public sealed class CatalogHttpClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}

internal static class CatalogHttp
{
    internal static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(20);

    internal static HttpStatusCode[] PassThroughStatuses { get; } =
        [HttpStatusCode.OK, HttpStatusCode.PartialContent, HttpStatusCode.RequestedRangeNotSatisfiable];
}
