namespace MusicHoarder.Api.Auth;

public class ResendOptions
{
    public const string SectionName = "Resend";

    /// <summary>
    /// Resend API key. When empty the magic-link sender falls back to logging the link to the
    /// console — fine for local dev, fine for first boot before keys are configured.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// "From" address (e.g. <c>auth@yourdomain.com</c>). Must be on a domain you've verified
    /// in the Resend dashboard.
    /// </summary>
    public string FromAddress { get; set; } = "noreply@musichoarder.local";
}
