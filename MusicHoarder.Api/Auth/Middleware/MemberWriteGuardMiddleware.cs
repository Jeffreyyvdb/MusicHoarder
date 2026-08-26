using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Auth.Middleware;

/// <summary>
/// Deny-by-default write guard for member accounts: a member may issue safe (GET/HEAD/OPTIONS)
/// requests freely, but every unsafe verb is rejected unless it matches an explicit allowance.
///
/// <para>
/// Deny-by-default is the whole point, for the same reason as
/// <see cref="DemoReadOnlyMiddleware"/>: guarding each mutation endpoint individually means the
/// next one somebody adds is member-writable because they forgot. Admin-only surfaces are
/// additionally gated by <c>RequireAdmin()</c>, but that is defence in depth, not the primary
/// control. <b>Extend the allowlist; never weaken the default.</b>
/// </para>
///
/// <para>
/// Matching is by route pattern, not prefix. A prefix rule is dangerous here: allowing
/// <c>/songs/</c> so a member can like a track would also allow <c>DELETE /songs/7</c>, which
/// soft-deletes the admin's row. Each entry therefore pins the verb and the exact shape, and may
/// additionally require a <see cref="Capability"/> the admin has granted.
/// </para>
///
/// <para>
/// Admins, demo, and anonymous requests pass through untouched — they are handled by the sibling
/// middlewares. Runs after <see cref="AuthenticationMiddleware"/> so the
/// <see cref="CurrentUser"/> is resolved.
/// </para>
/// </summary>
public sealed partial class MemberWriteGuardMiddleware
{
    /// <param name="Method">The exact verb. A rule never spans verbs.</param>
    /// <param name="Pattern">Anchored, case-insensitive path pattern.</param>
    /// <param name="Required">
    /// The capability the admin must have granted, or <see cref="Capability.None"/> for allowances
    /// that are inherent to holding an account at all (ending your own session, pairing your own
    /// phone, enrolling your own passkey).
    /// </param>
    private sealed record WriteRule(string Method, Regex Pattern, Capability Required);

    // Session and account self-management. None of these touch anyone else's data: they end the
    // caller's own session, mint a bearer for the caller's own session, enrol a passkey on the
    // caller's own account, accept an invite addressed to the caller, or switch between accounts
    // the browser already holds.
    private static readonly WriteRule[] Rules =
    [
        Exact("POST", "/api/auth/logout"),
        Exact("POST", "/api/auth/device-token"),
        Prefix("POST", "/api/auth/webauthn/authenticate"),
        Prefix("POST", "/api/auth/webauthn/register"),
        Prefix("POST", "/api/auth/webauthn/credentials"),
        Prefix("DELETE", "/api/auth/webauthn/credentials"),
        Exact("POST", "/api/invite/accept"),
        // Switch to another account already parked in this browser. Possession of the alts cookie
        // is the credential — see AccountSwitchService.
        Prefix("POST", "/api/auth/switch"),

        // NOTE: PATCH /api/auth/me (rename yourself) is deliberately NOT here, matching the
        // behaviour before this middleware was renamed. A member cannot set their own display
        // name. That is arguably wrong, but widening it is a product decision, not a refactor.

        // Listening state. These write a UserSongState row keyed to the caller, never the
        // grantor's song row — see SongsEndpoints.LikeSong. Note how narrow the patterns are:
        // "/songs/{id}/like" is allowed while "/songs/{id}" (soft-delete) is not.
        Song("POST", "like", Capability.TrackListening),
        Song("DELETE", "like", Capability.TrackListening),
        Song("POST", "played", Capability.TrackListening),

        // DEPRECATED alias of the three above; delete with the rest of /api/shared.
        Shared("POST", "like", Capability.TrackListening),
        Shared("DELETE", "like", Capability.TrackListening),
        Shared("POST", "played", Capability.TrackListening),
    ];

    private readonly RequestDelegate _next;

    public MemberWriteGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUserAccessor currentUser)
    {
        if (currentUser.User is not { } user || !user.IsMember)
        {
            await _next(context);
            return;
        }

        if (IsSafeMethod(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var rule = Match(context.Request.Method, context.Request.Path.Value);
        if (rule is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "member_write_denied" });
            return;
        }

        if (rule.Required != Capability.None && !user.Can(rule.Required))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "capability_required",
                capability = rule.Required.ToString(),
            });
            return;
        }

        await _next(context);
    }

    private static WriteRule? Match(string method, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var rule in Rules)
        {
            if (HttpMethods.Equals(rule.Method, method) && rule.Pattern.IsMatch(path))
                return rule;
        }
        return null;
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static WriteRule Exact(string method, string path) =>
        new(method, new Regex($"^{Regex.Escape(path)}/?$", RegexOptions.IgnoreCase), Capability.None);

    /// <summary>A route plus anything below it. Use only where every child is equally safe.</summary>
    private static WriteRule Prefix(string method, string path) =>
        new(method, new Regex($"^{Regex.Escape(path)}(/.*)?$", RegexOptions.IgnoreCase), Capability.None);

    private static WriteRule Song(string method, string action, Capability required) =>
        new(method, new Regex($@"^/songs/\d+/{Regex.Escape(action)}/?$", RegexOptions.IgnoreCase), required);

    private static WriteRule Shared(string method, string action, Capability required) =>
        new(method, new Regex($@"^/api/shared/songs/\d+/{Regex.Escape(action)}/?$", RegexOptions.IgnoreCase), required);
}
