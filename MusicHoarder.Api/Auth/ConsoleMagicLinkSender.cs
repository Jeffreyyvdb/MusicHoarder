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
        // user.Email is user-supplied; strip line breaks to prevent log forging (CWE-117).
        var safeEmail = user.Email.Replace("\r", "").Replace("\n", "");
        _logger.LogInformation(
            "[MAGIC LINK] for {Email} ({Role}): {Url}",
            safeEmail, user.Role, magicLinkUrl);
        return Task.CompletedTask;
    }
}
