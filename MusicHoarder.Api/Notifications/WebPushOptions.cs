namespace MusicHoarder.Api.Notifications;

/// <summary>
/// Web Push (browser notifications) configuration. Everything is optional: with no VAPID keys
/// configured the instance generates a pair on first use and keeps it in its database (the private
/// half wrapped by data protection), which is what a self-hoster wants. Configure a pair
/// (<c>WebPush__PublicKey</c> / <c>WebPush__PrivateKey</c>) only to keep subscriptions valid across
/// a database reset — never commit one.
/// </summary>
public class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>Off switch. When false no subscription is accepted and nothing is sent.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Uncompressed P-256 public key, base64url. Leave empty to generate one.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>The matching private scalar, base64url. A secret: environment or user-secrets only.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// The VAPID contact (<c>mailto:</c> or <c>https:</c>) push services may use to reach the
    /// operator. Empty: the frontend's public origin, else the admin's email.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// How long a chat message waits before it is pushed. A recipient who reads it within this
    /// window — they had the conversation open on some device — is not notified at all.
    /// </summary>
    public int ChatDelaySeconds { get; set; } = 4;
}
