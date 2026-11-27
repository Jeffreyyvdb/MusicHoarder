using System.Text.RegularExpressions;

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
    // The only non-safe requests a logged-in demo session legitimately makes: start a session, end
    // it, begin/finish a passkey login to switch into a real owner account (the browser carries
    // the stale demo cookie into the anonymous WebAuthn authenticate ceremony), or accept a friend
    // invite (same stale-cookie story: the invitee may have tried the demo first), or switch back
    // to an account already parked in its own browser (possession of the alts cookie is the
    // credential — see AccountSwitchService). The magic-link routes are here for the same
    // stale-cookie reason: /login keeps its form for a demo tab, and the callback forwards the
    // browser's cookies to /consume so the account switcher can park the demo session — blocking
    // them turned "Try the demo, then sign in with an emailed link" into "Sign-in didn't go
    // through". They grant nothing an anonymous caller lacks. The owner-only register/* routes are
    // deliberately NOT here. Everything else that mutates state is off-limits.
    // Each is one whole path, never a prefix: a prefix would hand the demo any route added later
    // whose path merely starts like one of these.
    private static readonly Regex[] AllowlistedWritePatterns =
    [
        Exact("/api/auth/demo-login"),
        Exact("/api/auth/logout"),
        Exact("/api/auth/switch"),
        Exact("/api/auth/request-link"),
        Exact("/api/auth/consume"),
        Exact("/api/auth/webauthn/authenticate/begin"),
        Exact("/api/auth/webauthn/authenticate/complete"),
        Exact("/api/invite/accept"),
    // A public share link's open/play beacons: someone who tried the demo on this instance and later
    // opens a share link carries the demo cookie along. The token-scoped /api/share/ surface is
    // anonymous (RequireAuthMiddleware), so allowing it here grants the demo nothing a stranger lacks.
    // Anchored per shape: it must not also cover the owner-only /api/shares.
        new(@"^/api/share/[^/]+/visit/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^/api/share/[^/]+/songs/\d+/play/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    private static Regex Exact(string path) => new($"^{Regex.Escape(path)}/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        foreach (var pattern in AllowlistedWritePatterns)
        {
            if (pattern.IsMatch(path))
                return true;
        }
        return false;
    }
}
