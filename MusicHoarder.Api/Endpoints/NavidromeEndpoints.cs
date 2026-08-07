using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Owner-only status probe for the Navidrome integration.
///
/// Navidrome like-sync has been running as a background service with no HTTP surface at all, so
/// there was no way for a user to tell whether it was configured, reachable, or silently off —
/// every other integration (Soulseek, Spotify, MH-to-MH sync) already had one. This mirrors
/// <see cref="SoulseekEndpoints"/>'s status route so the frontend can treat them alike.
/// </summary>
public static class NavidromeEndpoints
{
    /// <param name="Enabled">
    /// The <c>Navidrome:Enabled</c> flag on its own. Reported separately from
    /// <paramref name="Configured"/> because <see cref="NavidromeOptions.IsConfigured"/> folds the
    /// flag together with the credentials, and "turned off" needs different advice from
    /// "never set up".
    /// </param>
    /// <param name="Configured">Enabled *and* carrying a base URL, username and password.</param>
    /// <param name="Connected">The result of an authenticated ping. False whenever not configured.</param>
    /// <param name="BaseUrl">Which server this is pointed at; null unless configured.</param>
    public record NavidromeStatusResponse(bool Enabled, bool Configured, bool Connected, string? BaseUrl);

    public static void MapNavidromeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/navidrome").WithTags("Navidrome").RequireOwner();

        group.MapGet("/status", async (
                IOptionsMonitor<NavidromeOptions> options,
                INavidromeClient client,
                CancellationToken ct) =>
                Results.Ok(await BuildStatusAsync(options.CurrentValue, client, ct)))
            .WithName("GetNavidromeStatus")
            .WithSummary("Whether Navidrome is enabled, configured, and currently reachable.");
    }

    /// <summary>
    /// The status decision, split out from the route so it can be tested without a host.
    /// Never throws: <see cref="INavidromeClient.PingAsync"/> swallows transport and auth
    /// failures and reports false.
    /// </summary>
    public static async Task<NavidromeStatusResponse> BuildStatusAsync(
        NavidromeOptions options, INavidromeClient client, CancellationToken ct)
    {
        // Don't ping when unconfigured — the client would build a request against an empty base
        // URL, and "not set up" is not a connectivity question.
        if (!options.IsConfigured)
            return new NavidromeStatusResponse(options.Enabled, Configured: false, Connected: false, BaseUrl: null);

        var connected = await client.PingAsync(ct);
        return new NavidromeStatusResponse(Enabled: true, Configured: true, connected, options.BaseUrl);
    }
}
