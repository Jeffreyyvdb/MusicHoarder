namespace MusicHoarder.Api.Auth.Middleware;

/// <summary>
/// Enforces the listen-only contract for invited friends: a friend session may only issue safe
/// (GET/HEAD/OPTIONS) requests, plus the small allowlist of auth POSTs it needs to manage its own
/// session. Every other write verb is rejected with 403 before it reaches an endpoint.
///
/// <para>
/// Deny-by-default for the same reason as <see cref="DemoReadOnlyMiddleware"/>: rather than
/// guarding each mutation endpoint individually (and risking a newly-added one being forgotten),
/// friends are blocked from <em>any</em> unsafe method. Owner-only surfaces are additionally
/// gated by <c>RequireOwner()</c>; friend-visible music comes exclusively from the read-only
/// <c>/api/shared</c> endpoints. Owners, demo, and anonymous requests pass through untouched —
/// they are handled by the sibling middlewares. Runs after
/// <see cref="AuthenticationMiddleware"/> so the <see cref="CurrentUser"/> is resolved.
/// </para>
/// </summary>
public sealed class FriendReadOnlyMiddleware
{
    // The only non-safe requests a logged-in friend session legitimately makes: end its session,
    // pair a phone (mints a bearer for the friend's OWN session — same power as their cookie),
    // begin/finish a passkey login to switch into the owner account (the browser carries the
    // stale friend cookie into the anonymous WebAuthn authenticate ceremony), enrol or remove a
    // passkey on their OWN account (RequireRealAccount gates those endpoints, and they only ever
    // read the caller's own id — this is what lets a friend sign in on the phone with a passkey),
    // accept a fresh invite while a stale friend cookie is still present, switch to another account already
    // parked in its own browser (possession of the alts cookie is the credential — see
    // AccountSwitchService), or write their OWN listening state (/api/shared is the friend-facing
    // surface by definition: its writes touch FriendSongState rows keyed to the caller, never the
    // owner's data). Everything else that mutates state is off-limits.
    private static readonly string[] AllowlistedWritePaths =
    [
        "/api/auth/logout",
        "/api/auth/device-token",
        "/api/auth/switch",
        "/api/auth/webauthn/authenticate",
        "/api/auth/webauthn/register",
        "/api/auth/webauthn/credentials",
        "/api/invite/accept",
        "/api/shared/",
    ];

    private readonly RequestDelegate _next;

    public FriendReadOnlyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUserAccessor currentUser)
    {
        // Only friend accounts are constrained. Owners keep full access; the demo has its own
        // middleware; anonymous requests are handled by RequireAuthMiddleware.
        if (currentUser.User?.IsFriend != true)
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
        await context.Response.WriteAsJsonAsync(new { error = "friend_read_only" });
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
