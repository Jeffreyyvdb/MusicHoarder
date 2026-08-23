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
                IMagicLinkSender magicLinkSender,
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
                    // include the link directly for click-through. magicLinkInLogs is config-level
                    // (identical for known and unknown emails), so returning it stays
                    // enumeration-safe; the raw link itself remains Development-only — never ship
                    // it in Production responses.
                    return Results.Ok(new
                    {
                        ok = true,
                        magicLinkUrl = result?.DevMagicLinkUrl,
                        magicLinkInLogs = magicLinkSender.IsConsoleFallback,
                    });
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

        // Native-client variant of /consume: exchanges a magic-link token for a bearer access
        // token instead of a cookie. The token is the same protected session id the cookie
        // carries, so it hits the same server-side Session row (revocable, sliding lifetime).
        group.MapPost("/token", async (
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

                return Results.Ok(new AccessTokenResponse(
                    cookieService.Protect(session.Id), "Bearer", session.ExpiresAtUtc));
            })
            .WithName("AuthTokenExchange");

        // Mints a bearer token from an existing authenticated session (e.g. the web UI showing a
        // QR code / copyable token to pair a device). A separate Session row, so web logout
        // doesn't kill the device. Demo sessions never get here (DemoReadOnlyMiddleware blocks
        // the POST).
        group.MapPost("/device-token", async (
                HttpContext ctx,
                ICurrentUserAccessor accessor,
                IAuthService authService,
                ISessionCookieService cookieService,
                CancellationToken ct) =>
            {
                var user = accessor.User;
                if (user is null)
                    return Results.Json(new { error = "unauthenticated" }, statusCode: 401);

                var session = await authService.CreateDeviceSessionAsync(
                    user.Id,
                    ctx.Connection.RemoteIpAddress?.ToString(),
                    ctx.Request.Headers.UserAgent.ToString(),
                    ct);
                if (session is null)
                    return Results.Json(new { error = "user_unavailable" }, statusCode: 403);

                return Results.Ok(new AccessTokenResponse(
                    cookieService.Protect(session.Id), "Bearer", session.ExpiresAtUtc));
            })
            .WithName("AuthDeviceToken");

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
                // Cookie (browser) or bearer token (native client) — revoke whichever carried
                // this request's session.
                string? raw = null;
                if (ctx.Request.Cookies.TryGetValue(cookieService.CookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
                    raw = cookie;
                raw ??= BearerToken.TryRead(ctx);

                if (raw is not null)
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

    private static string ResolveFrontendBaseUrl(HttpContext ctx, FrontendOptions opts) =>
        FrontendUrlResolver.Resolve(ctx, opts);
}

public sealed record RequestLinkBody(string Email);
public sealed record ConsumeBody(string Token);

/// <summary>Bearer token issued to native clients; send as <c>Authorization: Bearer …</c>.</summary>
public sealed record AccessTokenResponse(string AccessToken, string TokenType, DateTime ExpiresAtUtc);
