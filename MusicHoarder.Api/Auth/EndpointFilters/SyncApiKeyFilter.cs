using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Auth.EndpointFilters;

/// <summary>
/// Machine-to-machine gate for the instance-sync endpoints — a parallel, additive scheme to the
/// cookie-session auth (no user/session is involved). The <c>/api/sync/check|upload</c> paths are
/// allowlisted in <c>RequireAuthMiddleware</c>, so THIS filter is the sole enforcement point:
/// <list type="bullet">
/// <item>404 unless this instance is configured as a receiver — the surface is invisible on every
/// other deployment, indistinguishable from the route not existing.</item>
/// <item>401 unless the <c>X-Sync-Key</c> header matches (fixed-time compare).</item>
/// <item>429 with per-IP damping after repeated failures — the endpoint is internet-facing by
/// design (no VPN), so cheap brute-force resistance on top of the 32+ char random key.</item>
/// </list>
/// </summary>
public sealed class SyncApiKeyFilter(
    IOptionsMonitor<SyncOptions> options,
    IMemoryCache cache,
    ILogger<SyncApiKeyFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "X-Sync-Key";

    private const int MaxFailuresPerWindow = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var opts = options.CurrentValue;
        if (!opts.IsReceiveConfigured)
            return Results.NotFound();

        var http = context.HttpContext;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var failureKey = $"sync_auth_failures:{ip}";

        if (cache.TryGetValue(failureKey, out int failures) && failures >= MaxFailuresPerWindow)
        {
            logger.LogWarning("Sync auth throttled for {Ip} ({Failures} recent failures)", ip, failures);
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var presented = http.Request.Headers[HeaderName].ToString();
        if (!KeysMatch(presented, opts.ApiKey))
        {
            cache.Set(failureKey, failures + 1, FailureWindow);
            logger.LogWarning("Sync auth failed from {Ip} (key {State})", ip,
                string.IsNullOrEmpty(presented) ? "missing" : "mismatch");
            return Results.Json(new { error = "invalid_sync_key" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    private static bool KeysMatch(string presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
            return false;
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
