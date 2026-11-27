using System.Net;
using Microsoft.Extensions.Primitives;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Logging;

namespace MusicHoarder.Api.Catalog;

/// <summary>
/// Relays a googlevideo audio stream to the browser, range requests included, so the page's CSP
/// (<c>media-src 'self'</c>) holds and the signed URL never reaches the client.
/// </summary>
internal static class CatalogAudioProxy
{
    internal static async Task<IResult> StreamAsync(
        string deezerId,
        HttpContext context,
        ICatalogAudioResolver resolver,
        CatalogHttpClient http,
        ILogger logger,
        CancellationToken ct)
    {
        var range = context.Request.Headers.Range;
        var client = http.Client;

        HttpResponseMessage? upstream = null;
        try
        {
            // The signed URL can die before its expire= says so (an IP change, a revoked token): once, drop it and ask again.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var audio = await resolver.ResolveAsync(deezerId, ct);
                if (!CatalogHosts.IsGoogleVideo(audio.StreamUrl))
                    return Results.Json(new { error = "youtube_failed", message = "The resolved audio URL is not a YouTube one." }, statusCode: StatusCodes.Status422UnprocessableEntity);

                using var headersBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headersBudget.CancelAfter(CatalogHttp.HeadersTimeout);
                upstream = await client.SendAsync(
                    BuildUpstreamRequest(audio.StreamUrl, range), HttpCompletionOption.ResponseHeadersRead, headersBudget.Token);

                if (attempt == 0 && IsStaleUrlStatus(upstream.StatusCode))
                {
                    logger.LogInformation("googlevideo answered {Status} for catalog track {DeezerId}; resolving again", (int)upstream.StatusCode, LogSanitizer.ForLog(deezerId));
                    upstream.Dispose();
                    upstream = null;
                    resolver.Invalidate(deezerId);
                    continue;
                }

                break;
            }

            if (upstream is null || !CatalogHttp.PassThroughStatuses.Contains(upstream.StatusCode))
            {
                logger.LogWarning("googlevideo answered {Status} for catalog track {DeezerId}", upstream is null ? 0 : (int)upstream.StatusCode, LogSanitizer.ForLog(deezerId));
                return Results.Json(new { error = "upstream_failed", message = "YouTube would not serve the audio." }, statusCode: StatusCodes.Status502BadGateway);
            }

            CopyHeaders(upstream, context.Response);
            await upstream.Content.CopyToAsync(context.Response.Body, ct);
            return Results.Empty;
        }
        catch (CatalogResolveException ex)
        {
            return CatalogEndpoints.ResolveFailure(ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The listener skipped or left; nothing to answer.
            return Results.Empty;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach googlevideo for catalog track {DeezerId}", LogSanitizer.ForLog(deezerId));
            if (context.Response.HasStarted)
                return Results.Empty;
            return Results.Json(new { error = "upstream_failed", message = "YouTube would not serve the audio." }, statusCode: StatusCodes.Status502BadGateway);
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    internal static bool IsStaleUrlStatus(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.Gone;

    internal static HttpRequestMessage BuildUpstreamRequest(string url, StringValues range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!StringValues.IsNullOrEmpty(range))
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        return request;
    }

    /// <summary>Status and the headers a player needs to seek, copied as the CDN sent them.</summary>
    internal static void CopyHeaders(HttpResponseMessage upstream, HttpResponse downstream)
    {
        downstream.StatusCode = (int)upstream.StatusCode;
        var content = upstream.Content.Headers;
        downstream.ContentType = content.ContentType?.ToString() ?? "audio/mp4";
        if (content.ContentLength is { } length)
            downstream.ContentLength = length;
        if (content.ContentRange is { } contentRange)
            downstream.Headers.ContentRange = contentRange.ToString();
        downstream.Headers.AcceptRanges = upstream.Headers.AcceptRanges.Count > 0
            ? string.Join(", ", upstream.Headers.AcceptRanges)
            : "bytes";
        downstream.Headers.CacheControl = "private, no-store";
    }
}
