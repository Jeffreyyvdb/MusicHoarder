namespace MusicHoarder.Api.Auth.Middleware;

/// <summary>
/// Enforces a strict read-only contract for the public demo account: a demo session may only
/// issue safe (GET/HEAD/OPTIONS) requests, plus a tiny allowlist of auth POSTs it needs to log
/// in and out. Every other write verb is rejected with 403 before it reaches an endpoint.
///
/// <para>
/// This is deny-by-default: rather than guarding each mutation endpoint individually (and risking
/// a newly-added one being forgotten), the demo is blocked from <em>any</em> unsafe method. Owners
/// and anonymous requests pass through untouched — anonymous traffic is already rejected upstream
/// by <see cref="RequireAuthMiddleware"/>. Runs after <see cref="AuthenticationMiddleware"/> so the
/// <see cref="CurrentUser"/> is resolved.
/// </para>
/// </summary>
public sealed class DemoReadOnlyMiddleware
{
    // The only non-safe requests a logged-in demo session legitimately makes: start a session and
    // end it. Everything else that mutates state is off-limits for the demo.
    private static readonly string[] AllowlistedWritePaths =
    [
        "/api/auth/demo-login",
        "/api/auth/logout",
    ];

    private readonly RequestDelegate _next;

    public DemoReadOnlyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUserAccessor currentUser)
    {
        // Only the demo account is constrained. Owners keep full access; anonymous requests are
        // handled by RequireAuthMiddleware.
        if (currentUser.User?.IsDemo != true)
        {
            await _next(context);
            return;
        }

        if (IsSafeMethod(context.Request.Method) || IsAllowlistedWrite(context.Request.Path.Value))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "demo_read_only" });
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsAllowlistedWrite(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var allowed in AllowlistedWritePaths)
        {
            if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
