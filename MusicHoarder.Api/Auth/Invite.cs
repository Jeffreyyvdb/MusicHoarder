using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// An owner-minted, email-bound, single-use invitation to create a <see cref="UserRole.Friend"/>
/// account. Follows <see cref="MagicLinkToken"/>'s posture — we store SHA-256 of the secret only,
/// never the raw token — because unlike a <see cref="Persistence.SongShare"/> link (one song's
/// audio) an invite mints an account. Consequence: the URL can only be shown at creation; a
/// "resend" rotates the token in place. Revoking sets <see cref="RevokedAtUtc"/>; the link then
/// 404s uniformly with expired/consumed/unknown ones.
/// </summary>
public class Invite
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The owner who minted the invite (scopes the management endpoints).</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Email the invite is bound to; becomes the friend account's login email.</summary>
    [Required, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Uppercase-invariant form of <see cref="Email"/> (see <see cref="User.Normalize"/>).</summary>
    [Required, MaxLength(256)]
    public string EmailNormalized { get; set; } = string.Empty;

    /// <summary>SHA-256 of the URL-safe token secret. 32 bytes.</summary>
    [Required]
    public byte[] TokenHash { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? ConsumedAtUtc { get; set; }

    /// <summary>The friend user the acceptance created (or re-enabled).</summary>
    public Guid? ConsumedByUserId { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public bool IsConsumable(DateTime nowUtc) =>
        ConsumedAtUtc is null && RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}
