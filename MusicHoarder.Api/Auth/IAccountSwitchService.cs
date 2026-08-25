namespace MusicHoarder.Api.Auth;

/// <summary>One signed-in account as seen by the account switcher.</summary>
public sealed record AccountView(Guid UserId, string Email, string Role, string? DisplayName, bool IsActive);

/// <summary>
/// Outcome of a cookie logout: the parked account promoted to active, or null when none was left
/// (both cookies cleared).
/// </summary>
public sealed record LogoutOutcome(AccountView? Fallback);

/// <summary>
/// Multi-account sign-in over the two session cookies: the active session cookie plus a parked
/// list (<see cref="ISessionCookieService.AltsCookieName"/>). Possession of the HttpOnly parked
/// cookie IS the credential for switching — this service never mints sessions and never resolves
/// a user by id outside the caller's own cookie set. It only writes response cookies; the caller
/// identity of the *current* request is never mutated (tenancy filters are per-request).
/// </summary>
public interface IAccountSwitchService
{
    /// <summary>
    /// Writes the new session as the active cookie. A still-valid previous session for a
    /// different user is parked; a previous session for the same user is replaced. Replaces the
    /// bare cookie write in every login flow (magic link, demo, passkey, invite accept).
    /// </summary>
    Task SignInAsync(HttpContext ctx, Session newSession, CancellationToken ct);

    /// <summary>
    /// Lists the accounts in this browser: active first, then parked by recency. Dead parked
    /// sessions are pruned and the cookie rewritten. Empty when the caller is anonymous.
    /// </summary>
    Task<IReadOnlyList<AccountView>> ListAccountsAsync(HttpContext ctx, CancellationToken ct);

    /// <summary>
    /// Makes the parked session for <paramref name="targetUserId"/> the active one, parking the
    /// current session. Null when no live parked session for that user exists in the caller's
    /// cookies (dead entries are pruned).
    /// </summary>
    Task<AccountView?> SwitchAsync(HttpContext ctx, Guid targetUserId, CancellationToken ct);

    /// <summary>
    /// Cookie logout. Revokes the active session (all of the active user's sessions when
    /// <paramref name="allForActiveUser"/>) and promotes the newest live parked account —
    /// except after "everywhere", which forgets (not revokes) other users' parked sessions and
    /// leaves the browser signed out.
    /// </summary>
    Task<LogoutOutcome> LogoutAsync(HttpContext ctx, bool allForActiveUser, CancellationToken ct);
}
