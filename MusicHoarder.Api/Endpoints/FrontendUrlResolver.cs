using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Endpoints;

internal static class FrontendUrlResolver
{
    /// <summary>
    /// Returns the public base URL of the frontend (where emailed/copied links should land).
    /// Prefers <see cref="FrontendOptions.PublicBaseUrl"/> when set (production), falling back to
    /// the current request's origin (typical in dev when Aspire wires both apps). Shared by the
    /// magic-link and friend-invite endpoints so their links can't disagree about the origin.
    /// </summary>
    internal static string Resolve(HttpContext ctx, FrontendOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.PublicBaseUrl))
            return opts.PublicBaseUrl.TrimEnd('/');

        // Fallback: use the request's origin. In Aspire dev the frontend reverse-proxies to the
        // API, so the Origin/Referer carries the frontend URL.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin)) return origin.TrimEnd('/');
        var referer = ctx.Request.Headers.Referer.ToString();
        if (!string.IsNullOrEmpty(referer))
        {
            try
            {
                var uri = new Uri(referer);
                return $"{uri.Scheme}://{uri.Authority}";
            }
            catch { }
        }
        // Last resort: same origin as this request.
        return $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    }
}
