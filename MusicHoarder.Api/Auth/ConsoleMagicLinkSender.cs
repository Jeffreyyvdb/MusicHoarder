namespace MusicHoarder.Api.Auth;

/// <summary>
/// Default sender when no Resend key is configured. Writes the link to the logs at
/// Information so it's visible in the Aspire dashboard / docker logs. Never used in
/// production once Resend is wired.
/// </summary>
public sealed class ConsoleMagicLinkSender : IMagicLinkSender
{
    private readonly ILogger<ConsoleMagicLinkSender> _logger;

    public ConsoleMagicLinkSender(ILogger<ConsoleMagicLinkSender> logger)
    {
        _logger = logger;
    }

    public bool IsConsoleFallback => true;

    public Task SendAsync(User user, string magicLinkUrl, CancellationToken ct = default)
    {
        // Log the server-generated user id + role rather than the email: the email is
        // user-influenced (log forging, CWE-117) and PII. Id + Role still identifies the
        // account in dev, where only the seeded Owner/Demo users exist.
        var sanitizedUrl = magicLinkUrl
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);

        _logger.LogInformation(
            "[MAGIC LINK] for user {UserId} ({Role}): {Url}",
            user.Id, user.Role, sanitizedUrl);
        return Task.CompletedTask;
    }
}
