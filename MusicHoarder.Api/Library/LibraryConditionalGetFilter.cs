using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace MusicHoarder.Api.Library;

/// <summary>
/// Conditional GET for the two library lists. Both are re-asked for on most page visits and by
/// every refresh, and the answer is usually the one the client already holds: thousands of rows
/// read, serialised and sent again to say that nothing changed. With this a client that sends back
/// the <c>ETag</c> it was given gets <c>304 Not Modified</c> and no body, for the price of
/// <see cref="ILibraryChangeStamp"/> instead of the list.
///
/// <para>
/// The tag is the caller's change stamp folded with the path and query string, so one account's
/// tag never matches another's and <c>?includeDeleted=true</c> never matches the default list. It is
/// a weak tag: the frontend proxy re-encodes the body on its way to the browser. Nothing is cached
/// here — the stamp is read from the database on every request, so a write, a revoked grant or a
/// different account takes effect on the next one. A client that sends no <c>If-None-Match</c>
/// (every shipped Android build) gets the same 200 and body as before, plus the header.
/// </para>
/// </summary>
public sealed class LibraryConditionalGetFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Read BEFORE the handler runs its own queries, never after. A write that lands in between
        // then leaves the client with a tag older than the rows it was sent, which costs it one
        // download it did not need. The other order would hand out a tag newer than the rows, and
        // the next request would be told those stale rows are current.
        var stamp = await http.RequestServices.GetRequiredService<ILibraryChangeStamp>()
            .ComputeAsync(http.RequestAborted);
        if (stamp is null)
            return await next(context);

        var etag = EtagFor(stamp, http.Request);
        if (IsHeldByClient(http.Request, etag))
        {
            http.Response.Headers.ETag = etag.ToString();
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var result = await next(context);
        // Only once the handler has produced the list: a failure must not go out carrying a tag.
        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK })
            http.Response.Headers.ETag = etag.ToString();
        return result;
    }

    /// <summary>
    /// The path and the query string as sent are part of the tag, so each variant of a list has its
    /// own. A parameter the handler ignores only splits one variant into two, which is harmless.
    /// </summary>
    internal static EntityTagHeaderValue EtagFor(string stamp, HttpRequest request)
    {
        var variant = $"{stamp}\n{request.PathBase}{request.Path}\n{request.QueryString}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(variant));
        return new EntityTagHeaderValue($"\"{WebEncoders.Base64UrlEncode(hash)}\"", isWeak: true);
    }

    /// <summary>
    /// Whether <c>If-None-Match</c> names this exact tag. <c>*</c> is not taken as a match: it says
    /// the client holds <i>a</i> copy, not which one, and answering it with a 304 would vouch for
    /// rows this request never looked at.
    /// </summary>
    private static bool IsHeldByClient(HttpRequest request, EntityTagHeaderValue etag)
    {
        foreach (var candidate in request.GetTypedHeaders().IfNoneMatch)
        {
            if (candidate.Compare(etag, useStrongComparison: false))
                return true;
        }

        return false;
    }
}

public static class LibraryConditionalGetExtensions
{
    /// <summary>
    /// Answer this route with <c>304 Not Modified</c> when the caller's library has not changed
    /// since the <c>ETag</c> they send back. Only for handlers whose every input
    /// <see cref="LibraryChangeStamp"/> covers — see the list there before adding a route.
    /// </summary>
    public static RouteHandlerBuilder WithLibraryChangeEtag(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<LibraryConditionalGetFilter>();
}
