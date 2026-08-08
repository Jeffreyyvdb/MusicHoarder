namespace MusicHoarder.Api.Auth;

public interface IMagicLinkSender
{
    /// <summary>
    /// Delivers the magic link to the user via whatever channel the implementation supports.
    /// Throws on permanent failure; the request endpoint surfaces that as a 503 so the client
    /// knows the email didn't go out.
    /// </summary>
    Task SendAsync(User user, string magicLinkUrl, CancellationToken ct = default);

    /// <summary>
    /// True when this sender is the no-email-fallback (links written to the logs). Drives the
    /// startup banner announcing that mode, the <c>magicLinkInLogs</c> flag on the
    /// /api/auth/request-link response (config-level, so enumeration-safe), and — in
    /// <c>Development</c> only — whether that response includes the raw link for click-through.
    /// </summary>
    bool IsConsoleFallback { get; }
}
