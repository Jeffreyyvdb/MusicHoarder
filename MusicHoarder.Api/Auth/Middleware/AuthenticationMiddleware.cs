namespace MusicHoarder.Api.Auth.Middleware;

/// <summary>
/// Reads the session cookie, validates the session, and stashes a <see cref="CurrentUser"/> on
/// <c>HttpContext.Items</c> for downstream access (via <see cref="ICurrentUserAccessor"/>).
/// Never rejects requests — that's <see cref="RequireAuthMiddleware"/>'s job.
/// </summary>
public sealed class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public AuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAuthService authService, ISessionCookieService cookieService)
    {
        if (!context.Request.Cookies.TryGetValue(cookieService.CookieName, out var raw) || string.IsNullOrEmpty(raw))
        {
            await _next(context);
            return;
        }

        var sessionId = cookieService.Unprotect(raw);
        if (sessionId is null)
        {
            // Cookie was tampered with or DP keys rotated; clear it.
            cookieService.Clear(context);
            await _next(context);
            return;
        }

        var resolved = await authService.ResolveSessionAsync(sessionId.Value, context.RequestAborted);
        if (resolved is null)
        {
            cookieService.Clear(context);
            await _next(context);
            return;
        }

        var (_, user) = resolved.Value;
        context.Items[HttpContextCurrentUserAccessor.HttpContextItemKey] = new CurrentUser(
            user.Id, user.Email, user.Role, user.DisplayName);

        await _next(context);
    }
}
