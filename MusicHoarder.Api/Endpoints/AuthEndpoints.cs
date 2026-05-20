using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/request-link", async (
                RequestLinkBody body,
                HttpContext ctx,
                IAuthService authService,
                IOptions<FrontendOptions> frontendOptions,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Email))
                    return Results.BadRequest(new { error = "email_required" });

                var frontendBase = ResolveFrontendBaseUrl(ctx, frontendOptions.Value);
                if (string.IsNullOrEmpty(frontendBase))
                    return Results.Json(new { error = "frontend_base_url_not_configured" }, statusCode: 500);

                try
                {
                    var result = await authService.RequestLinkAsync(
                        body.Email,
                        frontendBase,
                        ctx.Connection.RemoteIpAddress?.ToString(),
                        ctx.Request.Headers.UserAgent.ToString(),
                        ct);

                    // 200 OK whether or not the email exists, to avoid user enumeration. In dev,
                    // include the link directly for click-through.
                    return Results.Ok(new { ok = true, magicLinkUrl = result?.DevMagicLinkUrl });
                }
                catch (Exception)
                {
                    return Results.Json(new { error = "send_failed" }, statusCode: 503);
                }
            })
            .WithName("AuthRequestLink");

        group.MapPost("/consume", async (
                ConsumeBody body,
                HttpContext ctx,
                IAuthService authService,
                ISessionCookieService cookieService,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Token))
                    return Results.BadRequest(new { error = "token_required" });

                var session = await authService.ConsumeLinkAsync(
                    body.Token,
                    ctx.Connection.RemoteIpAddress?.ToString(),
                    ctx.Request.Headers.UserAgent.ToString(),
                    ct);
                if (session is null)
                    return Results.Json(new { error = "invalid_token" }, statusCode: 400);

                cookieService.Write(ctx, session.Id);
                return Results.Ok(new { ok = true });
            })
            .WithName("AuthConsume");

        group.MapPost("/demo-login", async (
                HttpContext ctx,
                IAuthService authService,
                ISessionCookieService cookieService,
                CancellationToken ct) =>
            {
                var session = await authService.StartDemoSessionAsync(
                    ctx.Connection.RemoteIpAddress?.ToString(),
                    ctx.Request.Headers.UserAgent.ToString(),
                    ct);
                if (session is null)
                    return Results.Json(new { error = "demo_unavailable" }, statusCode: 503);

                cookieService.Write(ctx, session.Id);
                return Results.Ok(new { ok = true });
            })
            .WithName("AuthDemoLogin");

        group.MapGet("/me", (HttpContext ctx, ICurrentUserAccessor accessor) =>
            {
                var user = accessor.User;
                if (user is null)
                    return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
                return Results.Ok(new
                {
                    id = user.Id,
                    email = user.Email,
                    role = user.Role.ToString(),
                    displayName = user.DisplayName,
                });
            })
            .WithName("AuthMe");

        group.MapPost("/logout", async (
                bool? all,
                HttpContext ctx,
                IAuthService authService,
                ISessionCookieService cookieService,
                CancellationToken ct) =>
            {
                if (ctx.Request.Cookies.TryGetValue(cookieService.CookieName, out var raw) && !string.IsNullOrEmpty(raw))
                {
                    var sessionId = cookieService.Unprotect(raw);
                    if (sessionId is not null)
                        await authService.RevokeAsync(sessionId.Value, allForUser: all == true, ct);
                }
                cookieService.Clear(ctx);
                return Results.Ok(new { ok = true });
            })
            .WithName("AuthLogout");

        return app;
    }

    /// <summary>
    /// Returns the public base URL of the frontend (where the email link should land). Prefers
    /// <see cref="FrontendOptions.PublicBaseUrl"/> when set (production), falling back to the
    /// current request's origin (typical in dev when Aspire wires both apps).
    /// </summary>
    private static string ResolveFrontendBaseUrl(HttpContext ctx, FrontendOptions opts)
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

public sealed record RequestLinkBody(string Email);
public sealed record ConsumeBody(string Token);
