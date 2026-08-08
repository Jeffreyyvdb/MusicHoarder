using MusicHoarder.Api.Artwork;

namespace MusicHoarder.Api.Endpoints;

public static class ArtistsEndpoints
{
    public static IEndpointRouteBuilder MapArtistsEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only, demo-visible (no RequireOwner): auth is still mandatory via
        // RequireAuthMiddleware, and the portrait cache is catalog data with no tenant scope.
        app.MapGet("/api/artists/image", GetArtistImage)
            .WithName("GetArtistImage")
            .WithSummary("Redirects to the artist's portrait (Deezer → Spotify, cached by normalized name); 404 when no provider has a verified portrait.")
            .WithTags("Library");

        return app;
    }

    internal static async Task<IResult> GetArtistImage(
        string name,
        IArtistImageService artistImages,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "name is required" });

        var url = await artistImages.GetImageUrlAsync(name, ct);
        if (url is null)
        {
            // Short-lived so a page full of unknown artists doesn't re-resolve per render, but a
            // newly cached portrait still shows up without a hard refresh.
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.NotFound();
        }

        // The browser caches the redirect target per tile; a day keeps grids instant while still
        // picking up refreshed CDN links reasonably soon.
        http.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.Redirect(url);
    }
}
