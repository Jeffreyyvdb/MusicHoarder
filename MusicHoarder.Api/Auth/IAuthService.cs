namespace MusicHoarder.Api.Auth;

public interface IAuthService
{
    /// <summary>
    /// Requests a magic link for the given email. Returns <c>null</c> when the email isn't
    /// a known user (caller maps that to 200 OK anyway to avoid user enumeration). When a
    /// user is found, a fresh token is created, any prior unconsumed tokens for the user
    /// are revoked, and the link is dispatched via <see cref="IMagicLinkSender"/>.
    /// </summary>
    Task<RequestLinkResult?> RequestLinkAsync(string email, string frontendBaseUrl, string? ip, string? userAgent, CancellationToken ct);

    /// <summary>
    /// Exchanges a raw token for a new session. Returns <c>null</c> when the token is
    /// invalid, expired, already consumed, or belongs to a disabled user.
    /// </summary>
    Task<Session?> ConsumeLinkAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct);

    /// <summary>Starts a session for the demo user (no link required — exposed publicly).</summary>
    Task<Session?> StartDemoSessionAsync(string? ip, string? userAgent, CancellationToken ct);

    /// <summary>Loads + refreshes a session if needed. Returns null when not valid.</summary>
    Task<(Session Session, User User)?> ResolveSessionAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Revokes one session or all sessions for the user.</summary>
    Task RevokeAsync(Guid sessionId, bool allForUser, CancellationToken ct);
}

public sealed record RequestLinkResult(string? DevMagicLinkUrl);
