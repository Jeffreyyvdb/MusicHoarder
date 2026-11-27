using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Find a song, hear it, queue it: Deezer search, its 30-second clip, and a full-song audition
/// through YouTube. The audio is served from here because the page may only load media from its own
/// origin. Admin only, like the import it feeds.
/// </summary>
public static class CatalogEndpoints
{
    /// <summary>The search answers at most this many tracks, whatever Deezer offers.</summary>
    internal const int MaxSearchResults = 25;
    internal const int MinQueryLength = 2;
    internal const int MaxQueryLength = 200;

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Catalog").RequireAdmin();

        group.MapGet("/search", Search)
            .WithName("CatalogSearch")
            .WithSummary("Search Deezer's catalog for tracks, marking those already on the wishlist.");

        group.MapGet("/preview/{deezerId}", Preview)
            .WithName("CatalogPreview")
            .WithSummary("A track's 30-second Deezer clip as audio/mpeg.");

        group.MapGet("/full/{deezerId}/resolve", ResolveFull)
            .WithName("CatalogFullResolve")
            .WithSummary("Find the YouTube audio for a track and warm the cache the stream route reads.");

        group.MapGet("/full/{deezerId}", StreamFull)
            .WithName("CatalogFullStream")
            .WithSummary("The full song, proxied from YouTube with range support.");

        return app;
    }

    internal static async Task<IResult> Search(
        string? q,
        IDeezerCatalogService deezer,
        MusicHoarderDbContext db,
        ILogger<CatalogTrack> logger,
        CancellationToken ct)
    {
        var query = q?.Trim() ?? "";
        if (query.Length < MinQueryLength)
            return Results.Ok(new CatalogSearchResponse([]));
        if (query.Length > MaxQueryLength)
            query = query[..MaxQueryLength];

        IReadOnlyList<DeezerCatalogTrack> hits;
        try
        {
            hits = await deezer.SearchTracksAsync(query, MaxSearchResults, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ProviderRateLimitedException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Deezer search failed for '{Query}'", LogSanitizer.ForLog(query));
            return Results.Json(
                new { error = "catalog_unavailable", message = "Deezer could not be reached. Try again in a moment." },
                statusCode: StatusCodes.Status502BadGateway);
        }

        var tracks = hits.Take(MaxSearchResults).ToList();
        var ids = tracks.Select(t => t.Id).ToList();

        // One query for the page: a track on the wishlist more than once shows its newest row.
        var rows = await db.WishlistItems.AsNoTracking()
            .Where(w => w.DeezerTrackId != null && ids.Contains(w.DeezerTrackId))
            .Select(w => new { w.DeezerTrackId, w.Status, w.UpdatedAtUtc })
            .ToListAsync(ct);
        var statusById = rows
            .GroupBy(r => r.DeezerTrackId!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAtUtc).First().Status.ToString());

        return Results.Ok(new CatalogSearchResponse(
            tracks.Select(t => CatalogTrack.From(t, statusById.GetValueOrDefault(t.Id))).ToList()));
    }

    internal static async Task<IResult> Preview(
        string deezerId,
        HttpContext http,
        CatalogPreviewService previews,
        ILogger<CatalogPreviewService> logger,
        CancellationToken ct)
    {
        if (!CatalogIds.IsValid(deezerId))
            return InvalidId();

        byte[]? clip;
        try
        {
            clip = await previews.GetClipAsync(deezerId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ProviderRateLimitedException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not fetch the preview clip for {DeezerId}", LogSanitizer.ForLog(deezerId));
            return Results.Json(
                new { error = "catalog_unavailable", message = "The preview could not be loaded. Try again in a moment." },
                statusCode: StatusCodes.Status502BadGateway);
        }

        if (clip is null)
            return Results.NotFound(new { error = "no_preview" });

        http.Response.Headers.CacheControl = "private, max-age=1800";
        return Results.File(clip, "audio/mpeg", enableRangeProcessing: true);
    }

    internal static async Task<IResult> ResolveFull(
        string deezerId,
        ICatalogAudioResolver resolver,
        CancellationToken ct)
    {
        if (!CatalogIds.IsValid(deezerId))
            return InvalidId();

        try
        {
            var audio = await resolver.ResolveAsync(deezerId, ct);
            return Results.Ok(new FullTrackResolveResponse(audio.YouTubeVideoId, audio.VideoTitle, audio.DurationMs));
        }
        catch (CatalogResolveException ex)
        {
            return ResolveFailure(ex);
        }
    }

    internal static Task<IResult> StreamFull(
        string deezerId,
        HttpContext context,
        ICatalogAudioResolver resolver,
        CatalogHttpClient audioHttp,
        ILogger<CatalogAudioResolver> logger,
        CancellationToken ct) =>
        CatalogIds.IsValid(deezerId)
            ? CatalogAudioProxy.StreamAsync(deezerId, context, resolver, audioHttp, logger, ct)
            : Task.FromResult(InvalidId());

    internal static IResult ResolveFailure(CatalogResolveException ex) => ex.Kind switch
    {
        CatalogResolveFailure.NotFound =>
            Results.NotFound(new { error = "not_found", message = ex.Message }),
        CatalogResolveFailure.CatalogUnavailable =>
            Results.Json(new { error = "catalog_unavailable", message = ex.Message }, statusCode: StatusCodes.Status502BadGateway),
        CatalogResolveFailure.YtDlpMissing =>
            Results.Json(new { error = "youtube_unavailable", message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ =>
            Results.UnprocessableEntity(new { error = "youtube_failed", message = ex.Message }),
    };

    private static IResult InvalidId() =>
        Results.BadRequest(new { error = "invalid_id", message = "A Deezer track id is digits only." });
}
