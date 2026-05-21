using Fido2NetLib;
using Microsoft.AspNetCore.DataProtection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// WebAuthn (passkey) ceremonies. Nested under <c>/api/auth/webauthn</c> so the unauthenticated
/// <c>authenticate/*</c> endpoints fall inside the <c>/api/auth/</c> allowlist; enrollment +
/// management re-require the owner via <see cref="RouteHandlerBuilderExtensions.RequireOwner"/>.
/// The in-flight ceremony challenge round-trips through a short-lived, data-protected cookie.
/// </summary>
public static class WebAuthnEndpoints
{
    private const string ProtectorPurpose = "MusicHoarder.WebAuthnChallenge.v1";
    private const string RegistrationCookie = "mh_webauthn_reg";
    private const string AuthenticationCookie = "mh_webauthn_auth";
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapWebAuthnEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/webauthn").WithTags("WebAuthn");

        // --- Registration (owner-only) ---------------------------------------------------------

        group.MapPost("/register/begin", async (
                HttpContext ctx,
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                IDataProtectionProvider dp,
                CancellationToken ct) =>
            {
                var userId = accessor.User!.Id;
                var options = await webAuthn.BeginRegistrationAsync(userId, ct);
                if (options is null)
                    return Results.Json(new { error = "user_unavailable" }, statusCode: 503);

                WriteChallengeCookie(ctx, dp, RegistrationCookie, options.ToJson());
                return Results.Text(options.ToJson(), "application/json");
            })
            .RequireOwner()
            .WithName("WebAuthnRegisterBegin");

        group.MapPost("/register/complete", async (
                RegisterCompleteBody body,
                HttpContext ctx,
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                IDataProtectionProvider dp,
                CancellationToken ct) =>
            {
                var stored = ReadChallengeCookie(ctx, dp, RegistrationCookie);
                ClearCookie(ctx, RegistrationCookie);
                if (stored is null)
                    return Results.Json(new { error = "challenge_expired" }, statusCode: 400);

                var originalOptions = CredentialCreateOptions.FromJson(stored);
                try
                {
                    var view = await webAuthn.CompleteRegistrationAsync(
                        accessor.User!.Id, body.Attestation, originalOptions, body.DisplayName ?? "Passkey", ct);
                    return Results.Ok(view);
                }
                catch (Fido2VerificationException ex)
                {
                    return Results.Json(new { error = "verification_failed", detail = ex.Message }, statusCode: 400);
                }
            })
            .RequireOwner()
            .WithName("WebAuthnRegisterComplete");

        group.MapGet("/credentials", async (
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                CancellationToken ct) =>
            {
                var creds = await webAuthn.ListCredentialsAsync(accessor.User!.Id, ct);
                return Results.Ok(creds);
            })
            .RequireOwner()
            .WithName("WebAuthnListCredentials");

        group.MapDelete("/credentials/{id:guid}", async (
                Guid id,
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                CancellationToken ct) =>
            {
                var removed = await webAuthn.DeleteCredentialAsync(accessor.User!.Id, id, ct);
                return removed ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "not_found" });
            })
            .RequireOwner()
            .WithName("WebAuthnDeleteCredential");

        // --- Authentication (anonymous — inside the /api/auth/ allowlist) ----------------------

        group.MapPost("/authenticate/begin", (
                HttpContext ctx,
                IWebAuthnService webAuthn,
                IDataProtectionProvider dp) =>
            {
                var options = webAuthn.BeginAuthentication();
                WriteChallengeCookie(ctx, dp, AuthenticationCookie, options.ToJson());
                return Results.Text(options.ToJson(), "application/json");
            })
            .WithName("WebAuthnAuthenticateBegin");

        group.MapPost("/authenticate/complete", async (
                AuthenticatorAssertionRawResponse assertion,
                HttpContext ctx,
                IWebAuthnService webAuthn,
                ISessionCookieService cookieService,
                IDataProtectionProvider dp,
                CancellationToken ct) =>
            {
                var stored = ReadChallengeCookie(ctx, dp, AuthenticationCookie);
                ClearCookie(ctx, AuthenticationCookie);
                if (stored is null)
                    return Results.Json(new { error = "challenge_expired" }, statusCode: 400);

                var originalOptions = AssertionOptions.FromJson(stored);
                var session = await webAuthn.CompleteAuthenticationAsync(
                    assertion,
                    originalOptions,
                    ctx.Connection.RemoteIpAddress?.ToString(),
                    ctx.Request.Headers.UserAgent.ToString(),
                    ct);
                if (session is null)
                    return Results.Json(new { error = "invalid_assertion" }, statusCode: 400);

                cookieService.Write(ctx, session.Id);
                return Results.Ok(new { ok = true });
            })
            .WithName("WebAuthnAuthenticateComplete");

        return app;
    }

    private static void WriteChallengeCookie(HttpContext ctx, IDataProtectionProvider dp, string name, string payload)
    {
        var protectedValue = dp.CreateProtector(ProtectorPurpose).Protect(payload);
        ctx.Response.Cookies.Append(name, protectedValue, new CookieOptions
        {
            Path = "/",
            Secure = ctx.Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(ChallengeTtl),
        });
    }

    private static string? ReadChallengeCookie(HttpContext ctx, IDataProtectionProvider dp, string name)
    {
        if (!ctx.Request.Cookies.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw))
            return null;
        try
        {
            return dp.CreateProtector(ProtectorPurpose).Unprotect(raw);
        }
        catch
        {
            return null;
        }
    }

    private static void ClearCookie(HttpContext ctx, string name) =>
        ctx.Response.Cookies.Delete(name, new CookieOptions
        {
            Path = "/",
            Secure = ctx.Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        });
}

public sealed record RegisterCompleteBody(AuthenticatorAttestationRawResponse Attestation, string? DisplayName);
