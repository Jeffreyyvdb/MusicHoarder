namespace MusicHoarder.Api.Auth.Middleware;

/// <summary>
/// Rejects unauthenticated requests to any non-allowlisted path. Runs after
/// <see cref="AuthenticationMiddleware"/>.
/// </summary>
public sealed class RequireAuthMiddleware
{
    private static readonly string[] AllowlistedPrefixes =
    [
        "/api/auth/",
        "/api/version", // prefix match — also covers /api/version/latest (the update-check endpoint)
        // Machine-to-machine instance sync: no cookie session exists on these requests. The
        // allowlist entry is NOT the gate — SyncApiKeyFilter on the endpoints enforces the shared
        // X-Sync-Key (and 404s unless this instance is a configured receiver). /api/sync/status is
        // deliberately absent: it's a normal cookie-authed endpoint.
        "/api/sync/check",
        "/api/sync/upload",
        "/health",
        "/alive",
        "/openapi",
        "/scalar",
    ];

    private readonly RequestDelegate _next;

    public RequireAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUserAccessor currentUser)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (IsAllowlisted(path))
        {
            await _next(context);
            return;
        }

        if (currentUser.User is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "unauthenticated" });
            return;
        }

        await _next(context);
    }

    private static bool IsAllowlisted(string path)
    {
        foreach (var prefix in AllowlistedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
