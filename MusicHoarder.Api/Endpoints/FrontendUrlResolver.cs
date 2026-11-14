using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Endpoints;

internal static class FrontendUrlResolver
{
    /// <summary>
    /// Returns the public base URL of the frontend (where emailed/copied links should land).
    /// Returns <see cref="FrontendOptions.PublicBaseUrl"/>, or an empty string when it is not
    /// configured; never derived from request headers. Shared by the
    /// magic-link and friend-invite endpoints so their links can't disagree about the origin.
    /// </summary>
    internal static string Resolve(HttpContext ctx, FrontendOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.PublicBaseUrl))
            return opts.PublicBaseUrl.TrimEnd('/');

        // Fail closed: Origin, Referer and Host are chosen by the caller, and this base goes into
        // emailed sign-in and invite links. Callers answer an empty result with
        // 500 frontend_base_url_not_configured.
        return string.Empty;
    }
}
