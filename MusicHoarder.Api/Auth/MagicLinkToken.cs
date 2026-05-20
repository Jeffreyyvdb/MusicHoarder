using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// Short-lived single-use token emailed to the user. We store SHA-256 of the secret only;
/// the raw secret never sees the database.
/// </summary>
public class MagicLinkToken
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>SHA-256 of the URL-safe token secret. 32 bytes.</summary>
    [Required]
    public byte[] TokenHash { get; set; } = [];

    public DateTime IssuedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? ConsumedAtUtc { get; set; }

    [MaxLength(64)]
    public string? RequestedFromIp { get; set; }

    [MaxLength(512)]
    public string? RequestedUserAgent { get; set; }

    public bool IsConsumable(DateTime nowUtc) =>
        ConsumedAtUtc is null && ExpiresAtUtc > nowUtc;
}
