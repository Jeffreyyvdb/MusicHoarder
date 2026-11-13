namespace MusicHoarder.Api.Auth.Middleware;

/// <summary>
/// Reads the session cookie (the browser, and the app shell's web view), validates the session,
/// and stashes a <see cref="CurrentUser"/> on <c>HttpContext.Items</c> for downstream access
/// (via <see cref="ICurrentUserAccessor"/>).
/// Never rejects requests — that's <see cref="RequireAuthMiddleware"/>'s job.
/// </summary>
public sealed class AuthenticationMiddleware
{
    /// <summary>
    /// The id of the session that authenticated the request, for a long-lived request (the
    /// playback stream) to re-check later that it has not been revoked since.
    /// </summary>
    public const string SessionIdItemKey = "__mh_session_id";

    private readonly RequestDelegate _next;

    public AuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAuthService authService, ISessionCookieService cookieService)
    {
        Guid? sessionId = null;

        if (context.Request.Cookies.TryGetValue(cookieService.CookieName, out var raw) && !string.IsNullOrEmpty(raw))
        {
            sessionId = cookieService.Unprotect(raw);
            if (sessionId is null)
                cookieService.Clear(context); // Cookie was tampered with or DP keys rotated; clear it.
        }

        if (sessionId is null)
        {
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

        var (session, user) = resolved.Value;
        context.Items[HttpContextCurrentUserAccessor.HttpContextItemKey] = new CurrentUser(
            user.Id, user.Email, user.Role, user.DisplayName, user.Capabilities);
        context.Items[SessionIdItemKey] = session.Id;

        await _next(context);
    }
}
