using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// A browser's Web Push subscription for one account: where to deliver a notification
/// (<see cref="Endpoint"/>, a URL at the browser vendor's push service) and the keys the payload is
/// encrypted to (RFC 8291). Unique per (account, endpoint): a browser that holds several signed-in
/// accounts (multi-account sign-in) has one endpoint, and each account that turned notifications on
/// there gets its own row. Deleted when the push service says the subscription is gone (404/410),
/// or when the account turns notifications off or signs out on that browser.
/// </summary>
public class WebPushSubscription
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    [MaxLength(2048)]
    public required string Endpoint { get; set; }

    /// <summary>The browser's P-256 public key, base64url (65-byte uncompressed point).</summary>
    [MaxLength(128)]
    public required string P256dh { get; set; }

    /// <summary>The 16-byte authentication secret, base64url.</summary>
    [MaxLength(64)]
    public required string Auth { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastDeliveredAtUtc { get; set; }

    /// <summary>Consecutive failed deliveries that were not a definitive "gone"; pruned past a cap.</summary>
    public int FailureCount { get; set; }
}

/// <summary>
/// The instance's VAPID key pair (RFC 8292), generated on first use when none is configured. One
/// row. The private key is stored protected by ASP.NET data protection, never in the clear.
/// </summary>
public class WebPushKeys
{
    public int Id { get; set; }

    /// <summary>Uncompressed P-256 public key, base64url — what browsers are handed to subscribe with.</summary>
    [MaxLength(128)]
    public required string PublicKey { get; set; }

    /// <summary>The private scalar, base64url, wrapped by IDataProtector.</summary>
    [MaxLength(1024)]
    public required string ProtectedPrivateKey { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
