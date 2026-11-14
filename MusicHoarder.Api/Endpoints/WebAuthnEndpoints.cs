using Fido2NetLib;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// WebAuthn (passkey) ceremonies. Nested under <c>/api/auth/webauthn</c> so the unauthenticated
/// <c>authenticate/*</c> endpoints fall inside the <c>/api/auth/</c> allowlist; enrollment +
/// management require a real (non-demo) account via
/// <see cref="RouteHandlerBuilderExtensions.RequireNonDemo"/>, an invited member included.
/// The in-flight ceremony challenge round-trips through a short-lived, data-protected cookie, and the
/// server also remembers each issued challenge until it is used once or its lifetime ends.
/// </summary>
public static class WebAuthnEndpoints
{
    private const string ProtectorPurpose = "MusicHoarder.WebAuthnChallenge.v1";
    private const string RegistrationCookie = "mh_webauthn_reg";
    private const string AuthenticationCookie = "mh_webauthn_auth";
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(5);
    private static readonly object ChallengeLock = new();

    public static IEndpointRouteBuilder MapWebAuthnEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/webauthn").WithTags("WebAuthn");

        // --- Registration (owner-only) ---------------------------------------------------------

        group.MapPost("/register/begin", async (
                HttpContext ctx,
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                IDataProtectionProvider dp,
                IMemoryCache cache,
                CancellationToken ct) =>
            {
                var userId = accessor.User!.Id;
                var options = await webAuthn.BeginRegistrationAsync(userId, ct);
                if (options is null)
                    return Results.Json(new { error = "user_unavailable" }, statusCode: 503);

                WriteChallengeCookie(ctx, dp, cache, RegistrationCookie, options.ToJson());
                return Results.Text(options.ToJson(), "application/json");
            })
            .RequireNonDemo()
            .WithName("WebAuthnRegisterBegin");

        group.MapPost("/register/complete", async (
                RegisterCompleteBody body,
                HttpContext ctx,
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                IDataProtectionProvider dp,
                IMemoryCache cache,
                CancellationToken ct) =>
            {
                var stored = ReadChallengeCookie(ctx, dp, cache, RegistrationCookie);
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
            .RequireNonDemo()
            .WithName("WebAuthnRegisterComplete");

        group.MapGet("/credentials", async (
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                CancellationToken ct) =>
            {
                var creds = await webAuthn.ListCredentialsAsync(accessor.User!.Id, ct);
                return Results.Ok(creds);
            })
            .RequireNonDemo()
            .WithName("WebAuthnListCredentials");

        group.MapGet("/credentials/signal", async (
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                CancellationToken ct) =>
            {
                var info = await webAuthn.GetSignalInfoAsync(accessor.User!.Id, ct);
                return info is null ? Results.NotFound(new { error = "user_unavailable" }) : Results.Ok(info);
            })
            .RequireNonDemo()
            .WithName("WebAuthnCredentialSignalInfo");

        group.MapDelete("/credentials/{id:guid}", async (
                Guid id,
                ICurrentUserAccessor accessor,
                IWebAuthnService webAuthn,
                CancellationToken ct) =>
            {
                var removed = await webAuthn.DeleteCredentialAsync(accessor.User!.Id, id, ct);
                return removed ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "not_found" });
            })
            .RequireNonDemo()
            .WithName("WebAuthnDeleteCredential");

        // --- Authentication (anonymous — inside the /api/auth/ allowlist) ----------------------

        group.MapPost("/authenticate/begin", (
                HttpContext ctx,
                IWebAuthnService webAuthn,
                IDataProtectionProvider dp,
                IMemoryCache cache) =>
            {
                var options = webAuthn.BeginAuthentication();
                WriteChallengeCookie(ctx, dp, cache, AuthenticationCookie, options.ToJson());
                return Results.Text(options.ToJson(), "application/json");
            })
            .WithName("WebAuthnAuthenticateBegin");

        group.MapPost("/authenticate/complete", async (
                AuthenticatorAssertionRawResponse assertion,
                HttpContext ctx,
                IWebAuthnService webAuthn,
                IAccountSwitchService accountSwitch,
                IDataProtectionProvider dp,
                IMemoryCache cache,
                CancellationToken ct) =>
            {
                var stored = ReadChallengeCookie(ctx, dp, cache, AuthenticationCookie);
                ClearCookie(ctx, AuthenticationCookie);
                if (stored is null)
                    return Results.Json(new { error = "challenge_expired" }, statusCode: 400);

                var originalOptions = AssertionOptions.FromJson(stored);
                var result = await webAuthn.CompleteAuthenticationAsync(
                    assertion,
                    originalOptions,
                    ctx.Connection.RemoteIpAddress?.ToString(),
                    ctx.Request.Headers.UserAgent.ToString(),
                    ct);
                if (result.Session is not { } session)
                    return SignInRefused(result.Failure);

                await accountSwitch.SignInAsync(ctx, session, ct);
                return Results.Ok(new { ok = true });
            })
            .WithName("WebAuthnAuthenticateComplete");

        return app;
    }

    /// <summary>
    /// A refused passkey sign-in. A passkey this server no longer has (removed here, still in the
    /// password manager) gets its own code, so the browser can tell the password manager to drop it
    /// (<c>PublicKeyCredential.signalUnknownCredential</c>). Older clients treat any 400 as a failed
    /// sign-in.
    /// </summary>
    private static IResult SignInRefused(PasskeySignInFailure failure) =>
        Results.Json(
            new { error = failure == PasskeySignInFailure.UnknownCredential ? "unknown_credential" : "invalid_assertion" },
            statusCode: 400);

    private static string CacheKey(string name, string protectedValue) =>
        "webauthn-challenge:" + name + ":" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(protectedValue)));

    private static void WriteChallengeCookie(
        HttpContext ctx, IDataProtectionProvider dp, IMemoryCache cache, string name, string payload)
    {
        var protectedValue = dp.CreateProtector(ProtectorPurpose).Protect(payload);
        cache.Set(CacheKey(name, protectedValue), true, ChallengeTtl);
        ctx.Response.Cookies.Append(name, protectedValue, new CookieOptions
        {
            Path = "/",
            Secure = ctx.Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(ChallengeTtl),
        });
    }

    private static string? ReadChallengeCookie(
        HttpContext ctx, IDataProtectionProvider dp, IMemoryCache cache, string name)
    {
        if (!ctx.Request.Cookies.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw))
            return null;

        // A challenge is good for one attempt within its lifetime: take it out of the server-side
        // record before anything else, so a replayed cookie is refused whatever the client holds.
        var key = CacheKey(name, raw);
        lock (ChallengeLock)
        {
            if (!cache.TryGetValue(key, out _))
                return null;
            cache.Remove(key);
        }

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

