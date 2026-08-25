namespace MusicHoarder.Api.Auth;

public interface IAuthService
{
    /// <summary>
    /// Requests a magic link for the given email. Returns <c>null</c> when the email isn't
    /// a known user (caller maps that to 200 OK anyway to avoid user enumeration). When a
    /// user is found, a fresh token is created, any prior unconsumed tokens for the user
    /// are revoked, and the link is dispatched via <see cref="IMagicLinkSender"/>.
    /// <paramref name="client"/> is <c>"app"</c> when a native app requested the link: the
    /// emailed URL then lands on the browser handoff page instead of consuming immediately,
    /// so the token stays valid until the app exchanges it at <c>/api/auth/token</c>.
    /// </summary>
    Task<RequestLinkResult?> RequestLinkAsync(string email, string frontendBaseUrl, string? client, string? ip, string? userAgent, CancellationToken ct);

    /// <summary>
    /// Exchanges a raw token for a new session. Returns <c>null</c> when the token is
    /// invalid, expired, already consumed, or belongs to a disabled user.
    /// </summary>
    Task<Session?> ConsumeLinkAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct);

    /// <summary>Starts a session for the demo user (no link required — exposed publicly).</summary>
    Task<Session?> StartDemoSessionAsync(string? ip, string? userAgent, CancellationToken ct);

    /// <summary>
    /// Creates a fresh session for an already-authenticated user, used to mint bearer tokens for
    /// native clients. A separate row from the caller's own session, so revoking the browser
    /// session doesn't log the device out (and vice versa). Returns <c>null</c> when the user is
    /// unknown or disabled.
    /// </summary>
    Task<Session?> CreateDeviceSessionAsync(Guid userId, string? ip, string? userAgent, CancellationToken ct);

    /// <summary>Loads + refreshes a session if needed. Returns null when not valid.</summary>
    Task<(Session Session, User User)?> ResolveSessionAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Loads the given sessions in one query and returns only the live ones (active, user not
    /// disabled), in input order. Read-only: no sliding renewal — used by the account switcher to
    /// validate the active + parked cookie set without a write per parked session.
    /// </summary>
    Task<IReadOnlyList<(Session Session, User User)>> ResolveSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct);

    /// <summary>Revokes one session or all sessions for the user.</summary>
    Task RevokeAsync(Guid sessionId, bool allForUser, CancellationToken ct);

    /// <summary>
    /// Mints (or rotates) the friend invite for the given email. When an active unconsumed invite
    /// already exists for the email, its token is replaced and its expiry reset — the previous
    /// link stops working — because only the hash is stored, so "resend" cannot re-emit the old
    /// URL. Returns <c>null</c> when the email belongs to an existing non-Friend user (the owner
    /// can't invite themselves or the demo). The raw token is only ever available here.
    /// </summary>
    Task<InviteMintResult?> CreateOrRotateInviteAsync(Guid ownerUserId, string email, CancellationToken ct);

    /// <summary>
    /// Resolves a raw invite token without consuming it, so the invite page can render before the
    /// recipient commits (and so an email scanner's GET prefetch can't burn the single use).
    /// Returns <c>null</c> for unknown/expired/revoked/consumed tokens.
    /// </summary>
    Task<InvitePeekResult?> PeekInviteAsync(string rawToken, CancellationToken ct);

    /// <summary>
    /// Consumes an invite: creates the <see cref="UserRole.Friend"/> account for the bound email
    /// (or re-enables a previously removed friend with the same email) and starts a session.
    /// Returns <c>null</c> when the token is invalid or the email meanwhile belongs to a
    /// non-Friend user.
    /// </summary>
    Task<Session?> AcceptInviteAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct);
}

public sealed record RequestLinkResult(string? DevMagicLinkUrl);

/// <summary>The freshly minted invite plus its raw token (shown once, never stored).</summary>
public sealed record InviteMintResult(Invite Invite, string RawToken);

public sealed record InvitePeekResult(string InviterName, string Email);
