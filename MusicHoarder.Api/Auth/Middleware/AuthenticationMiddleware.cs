namespace MusicHoarder.Api.Auth.Middleware;

/// <summary>
/// Reads the session cookie (browser clients) or the <c>Authorization: Bearer</c> token (native
/// clients — same protected session id, see <see cref="BearerToken"/>), validates the session,
/// and stashes a <see cref="CurrentUser"/> on <c>HttpContext.Items</c> for downstream access
/// (via <see cref="ICurrentUserAccessor"/>).
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
        Guid? sessionId = null;
        var fromCookie = false;

        if (context.Request.Cookies.TryGetValue(cookieService.CookieName, out var raw) && !string.IsNullOrEmpty(raw))
        {
            sessionId = cookieService.Unprotect(raw);
            if (sessionId is null)
                cookieService.Clear(context); // Cookie was tampered with or DP keys rotated; clear it.
            else
                fromCookie = true;
        }

        if (sessionId is null && BearerToken.TryRead(context) is { } bearer)
            sessionId = cookieService.Unprotect(bearer);

        if (sessionId is null)
        {
            await _next(context);
            return;
        }

        var resolved = await authService.ResolveSessionAsync(sessionId.Value, context.RequestAborted);
        if (resolved is null)
        {
            // A dead bearer session has nothing to clear client-side; the 401 downstream is enough.
            if (fromCookie)
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
