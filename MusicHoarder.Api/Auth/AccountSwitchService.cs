namespace MusicHoarder.Api.Auth;

public sealed class AccountSwitchService : IAccountSwitchService
{
    /// <summary>Most parked accounts kept per browser (active cookie excluded).</summary>
    internal const int MaxParkedSessions = 4;

    private readonly IAuthService _authService;
    private readonly ISessionCookieService _cookies;

    public AccountSwitchService(IAuthService authService, ISessionCookieService cookies)
    {
        _authService = authService;
        _cookies = cookies;
    }

    public async Task SignInAsync(HttpContext ctx, Session newSession, CancellationToken ct)
    {
        // Candidates to park, newest first: the session being replaced, then the already-parked
        // list. Dead entries, the new user's own sessions (same-user re-login replaces, never
        // parks itself), and per-user duplicates fall out in RebuildParked.
        var candidates = new List<Guid>();
        var oldActiveId = ReadActiveSessionId(ctx);
        if (oldActiveId is not null) candidates.Add(oldActiveId.Value);
        candidates.AddRange(_cookies.ReadAlts(ctx));

        var live = await _authService.ResolveSessionsAsync(candidates, ct).ConfigureAwait(false);
        var parked = RebuildParked(live, excludeUserId: newSession.UserId, excludeSessionId: newSession.Id);

        _cookies.Write(ctx, newSession.Id);
        _cookies.WriteAlts(ctx, parked);
    }

    public async Task<IReadOnlyList<AccountView>> ListAccountsAsync(HttpContext ctx, CancellationToken ct)
    {
        var activeId = ReadActiveSessionId(ctx);
        if (activeId is null) return [];

        var alts = _cookies.ReadAlts(ctx);
        var candidates = new List<Guid> { activeId.Value };
        candidates.AddRange(alts);
        var live = await _authService.ResolveSessionsAsync(candidates, ct).ConfigureAwait(false);

        var active = live.FirstOrDefault(p => p.Session.Id == activeId.Value);
        if (active.Session is null) return [];

        var parked = RebuildParked(live, excludeUserId: active.User.Id, excludeSessionId: activeId.Value);
        if (!parked.SequenceEqual(alts))
            _cookies.WriteAlts(ctx, parked); // self-healing: dead/duplicate entries pruned on read

        var views = new List<AccountView> { ToView(active.User, isActive: true) };
        foreach (var id in parked)
        {
            var pair = live.First(p => p.Session.Id == id);
            views.Add(ToView(pair.User, isActive: false));
        }
        return views;
    }

    public async Task<AccountView?> SwitchAsync(HttpContext ctx, Guid targetUserId, CancellationToken ct)
    {
        var activeId = ReadActiveSessionId(ctx);
        var alts = _cookies.ReadAlts(ctx);
        if (alts.Count == 0) return null;

        var candidates = new List<Guid>(alts);
        if (activeId is not null) candidates.Insert(0, activeId.Value);
        var live = await _authService.ResolveSessionsAsync(candidates, ct).ConfigureAwait(false);

        // The target must already be in the caller's parked cookie — possession is the
        // credential; a user id that is merely valid in the DB is not switchable.
        var target = live.FirstOrDefault(p => p.Session.Id != activeId && p.User.Id == targetUserId
            && alts.Contains(p.Session.Id));
        if (target.Session is null)
        {
            // Prune whatever made the client think this target existed.
            var pruned = activeId is null
                ? RebuildParked(live, excludeUserId: null, excludeSessionId: null)
                : RebuildParked(live,
                    excludeUserId: live.FirstOrDefault(p => p.Session.Id == activeId.Value).User?.Id,
                    excludeSessionId: activeId.Value);
            if (!pruned.SequenceEqual(alts)) _cookies.WriteAlts(ctx, pruned);
            return null;
        }

        var parked = RebuildParked(live, excludeUserId: targetUserId, excludeSessionId: target.Session.Id);
        _cookies.Write(ctx, target.Session.Id);
        _cookies.WriteAlts(ctx, parked);
        return ToView(target.User, isActive: true);
    }

    public async Task<LogoutOutcome> LogoutAsync(HttpContext ctx, bool allForActiveUser, CancellationToken ct)
    {
        var activeId = ReadActiveSessionId(ctx);
        var alts = _cookies.ReadAlts(ctx);

        if (activeId is not null)
            await _authService.RevokeAsync(activeId.Value, allForUser: allForActiveUser, ct).ConfigureAwait(false);

        if (allForActiveUser)
        {
            // A per-account security action: other users' parked sessions are forgotten here but
            // NOT revoked (revoking them would exceed the acting account's authority — e.g. it
            // would unpair a friend's phone). No fallback either: after "sign out everywhere" the
            // browser lands signed out, not silently inside another account.
            _cookies.Clear(ctx);
            _cookies.ClearAlts(ctx);
            return new LogoutOutcome(null);
        }

        // Promote the newest still-live parked account, if any.
        var live = await _authService.ResolveSessionsAsync(alts, ct).ConfigureAwait(false);
        var promoted = live.FirstOrDefault(p => p.Session.Id != activeId);
        if (promoted.Session is null)
        {
            _cookies.Clear(ctx);
            _cookies.ClearAlts(ctx);
            return new LogoutOutcome(null);
        }

        var parked = RebuildParked(live, excludeUserId: promoted.User.Id, excludeSessionId: promoted.Session.Id);
        _cookies.Write(ctx, promoted.Session.Id);
        _cookies.WriteAlts(ctx, parked);
        return new LogoutOutcome(ToView(promoted.User, isActive: true));
    }

    private Guid? ReadActiveSessionId(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(_cookies.CookieName, out var raw) || string.IsNullOrEmpty(raw))
            return null;
        return _cookies.Unprotect(raw);
    }

    /// <summary>
    /// Filters live (session, user) pairs — given newest first — into the parked list: one
    /// session per user, never the excluded user/session, capped at <see cref="MaxParkedSessions"/>.
    /// </summary>
    private static List<Guid> RebuildParked(
        IReadOnlyList<(Session Session, User User)> liveNewestFirst,
        Guid? excludeUserId,
        Guid? excludeSessionId)
    {
        var parked = new List<Guid>(MaxParkedSessions);
        var seenUsers = new HashSet<Guid>();
        foreach (var (session, user) in liveNewestFirst)
        {
            if (session.Id == excludeSessionId || user.Id == excludeUserId) continue;
            if (!seenUsers.Add(user.Id)) continue;
            parked.Add(session.Id);
            if (parked.Count == MaxParkedSessions) break;
        }
        return parked;
    }

    private static AccountView ToView(User user, bool isActive) =>
        new(user.Id, user.Email, user.Role.ToString(), user.DisplayName, isActive);
}
