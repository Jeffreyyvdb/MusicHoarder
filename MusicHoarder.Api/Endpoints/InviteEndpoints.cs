using MusicHoarder.Api.Auth;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The anonymous face of friend invites — what the invite-link recipient hits. Allowlisted in
/// <c>RequireAuthMiddleware</c>; the token is the whole capability and resolves past the query
/// filters (the clicker may be anonymous or carry a stale demo/friend cookie).
///
/// <para>
/// GET peeks without consuming — an email scanner's link prefetch must not burn the single-use
/// token — so acceptance is a deliberate POST (which is why this flow doesn't mirror
/// <c>/api/auth/consume</c>'s consume-on-landing shape). The accept response writes the session
/// cookie, overwriting any stale one, exactly like demo-login.
/// </para>
/// </summary>
public static class InviteEndpoints
{
    public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invite").WithTags("Invites");

        group.MapGet("/{token}", PeekInvite)
            .WithName("PeekInvite")
            .WithSummary("Anonymous: who invited you and for which email, without consuming the token.");
        group.MapPost("/accept", AcceptInvite)
            .WithName("AcceptInvite")
            .WithSummary("Anonymous: redeem an invite — creates the friend account and signs it in.");
        group.MapPost("/accept-token", AcceptInviteToken)
            .WithName("AcceptInviteToken")
            .WithSummary("Anonymous: redeem an invite for a bearer token — the native-client variant of accept.");

        return app;
    }

    public sealed record AcceptInviteBody(string Token);

    internal static async Task<IResult> PeekInvite(string token, IAuthService authService, CancellationToken ct)
    {
        var peek = await authService.PeekInviteAsync(token, ct);
        return peek is null ? InviteNotFound() : Results.Ok(new { peek.InviterName, peek.Email });
    }

    internal static async Task<IResult> AcceptInvite(
        AcceptInviteBody body,
        HttpContext ctx,
        IAuthService authService,
        ISessionCookieService cookieService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Token))
            return Results.BadRequest(new { error = "token_required" });

        var session = await authService.AcceptInviteAsync(
            body.Token,
            ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.Request.Headers.UserAgent.ToString(),
            ct);
        if (session is null)
            return Results.Json(new { error = "invalid_token" }, statusCode: 400);

        cookieService.Write(ctx, session.Id);
        return Results.Ok(new { ok = true });
    }

    /// <summary>
    /// The native-client variant of <see cref="AcceptInvite"/>, mirroring how <c>/api/auth/token</c>
    /// pairs with <c>/api/auth/consume</c>: same single-use redemption, but the session comes back as
    /// a bearer instead of a cookie — what the Android app pairs with when an invite App Link is
    /// accepted in the app.
    /// </summary>
    internal static async Task<IResult> AcceptInviteToken(
        AcceptInviteBody body,
        HttpContext ctx,
        IAuthService authService,
        ISessionCookieService cookieService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Token))
            return Results.BadRequest(new { error = "token_required" });

        var session = await authService.AcceptInviteAsync(
            body.Token,
            ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.Request.Headers.UserAgent.ToString(),
            ct);
        if (session is null)
            return Results.Json(new { error = "invalid_token" }, statusCode: 400);

        return Results.Ok(new AccessTokenResponse(
            cookieService.Protect(session.Id), "Bearer", session.ExpiresAtUtc));
    }

    /// <summary>Uniform 404 for unknown, expired, revoked, and consumed invites — no oracle for probing.</summary>
    private static IResult InviteNotFound() =>
        Results.NotFound(new { message = "This invite does not exist or has expired." });
}
