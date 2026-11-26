using System.Security.Cryptography;
using System.Text;
using MusicHoarder.Api.Artwork;

namespace MusicHoarder.Api.Endpoints;

public static class ArtistsEndpoints
{
    /// <summary>
    /// Most names one bulk request may carry. The client also splits by encoded length (long or
    /// non-Latin names percent-encode to several times their length), so a request line stays
    /// under Kestrel's 8 KB limit; this cap is the server's backstop.
    /// </summary>
    internal const int MaxBulkNames = 150;

    /// <summary>
    /// The name separator in the <c>names</c> query parameter. A newline, because artist names may
    /// contain commas ("Crosby, Stills &amp; Nash") but never a newline; the client encodes each name
    /// with <c>encodeURIComponent</c>, so the separator survives decoding intact.
    /// </summary>
    internal const char NameSeparator = '\n';

    public static IEndpointRouteBuilder MapArtistsEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only, demo-visible (no RequireAdmin): auth is still mandatory via
        // RequireAuthMiddleware, and the portrait cache is catalog data with no tenant scope.
        app.MapGet("/api/artists/image", GetArtistImage)
            .WithName("GetArtistImage")
            .WithSummary("Redirects to the artist's portrait (Spotify → Deezer, cached by normalized name); 404 when no provider has a verified portrait.")
            .WithTags("Library");

        // The artists grid asks for every visible artist's portrait in one request instead of one
        // redirect per tile; cached rows come back at once, missing/stale known artists are enqueued
        // for the background prefetcher. The ETag makes repeat visits a 304.
        app.MapGet("/api/artists/images", GetArtistImages)
            .WithName("GetArtistImages")
            .WithSummary("Bulk cached artist portraits: ?names=a,b,c → { name: { url, large } | null }. Missing/stale known artists are enqueued for background refresh.")
            .WithTags("Library");

        return app;
    }

    internal static async Task<IResult> GetArtistImage(
        string name,
        IArtistImageService artistImages,
        HttpContext http,
        bool large = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "name is required" });

        var (url, pending) = await artistImages.LookupAsync(name, large, ct);
        if (url is null)
        {
            // A lookup was just queued: the portrait may exist within seconds, so the miss is only
            // remembered briefly. Otherwise the providers have no portrait (or the name is not an
            // artist anyone has): an hour keeps a page of those from re-asking on every render.
            http.Response.Headers.CacheControl = pending ? "private, max-age=60" : "private, max-age=3600";
            return Results.NotFound();
        }

        // The browser caches the redirect target per tile; a day keeps grids instant while still
        // picking up refreshed CDN links reasonably soon.
        http.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.Redirect(url);
    }

    /// <summary>One row per requested display name: the grid-sized <c>url</c> and, when the provider offered one, the <c>large</c> hero portrait.</summary>
    internal sealed record ArtistImageEntry(string? Url, string? Large);

    internal static async Task<IResult> GetArtistImages(
        string? names,
        IArtistImageService artistImages,
        HttpContext http,
        CancellationToken ct)
    {
        var requested = (names ?? "")
            .Split(NameSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0)
            return Results.BadRequest(new { error = "names is required" });
        if (requested.Count > MaxBulkNames)
            return Results.BadRequest(new { error = $"at most {MaxBulkNames} names per request" });

        var rows = await artistImages.GetImagesMapAsync(requested, ct);

        var images = new Dictionary<string, ArtistImageEntry?>(requested.Count, StringComparer.Ordinal);
        var etagMaterial = new StringBuilder();
        foreach (var name in requested)
        {
            var row = rows.TryGetValue(name, out var r) ? r : null;
            images[name] = row is null or { ImageUrl: null }
                ? null
                : new ArtistImageEntry(row.ImageUrl, row.ImageUrlLarge ?? row.ImageUrl);
            etagMaterial.Append(name).Append('\0')
                .Append(row?.ImageUrl ?? "-").Append('\0')
                .Append(row?.ImageUrlLarge ?? "-").Append('\0')
                .Append(row?.FetchedAtUtc.Ticks ?? 0).Append('\n');
        }

        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(etagMaterial.ToString())))[..16] + "\"";
        http.Response.Headers.ETag = etag;
        // Revalidate at most once a minute; the background prefetcher fills gaps and the next
        // revalidation picks them up.
        http.Response.Headers.CacheControl = "private, max-age=60";

        if (string.Equals(http.Request.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.Json(new { images });
    }
}
