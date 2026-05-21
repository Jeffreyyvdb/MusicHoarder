using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// A registered passkey (WebAuthn/FIDO2 credential) bound to a <see cref="User"/>. The
/// <see cref="CredentialId"/> is the authenticator-issued handle returned in every assertion;
/// <see cref="PublicKey"/> verifies the assertion signature and <see cref="SignCount"/> guards
/// against cloned authenticators.
/// </summary>
public class WebAuthnCredential
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>Authenticator-issued credential id. Unique across all users.</summary>
    [Required]
    public byte[] CredentialId { get; set; } = [];

    /// <summary>COSE-encoded public key used to verify assertion signatures.</summary>
    [Required]
    public byte[] PublicKey { get; set; } = [];

    /// <summary>
    /// Stored signature counter. Stored as <c>long</c> for portable provider mapping; the FIDO2
    /// library works in <c>uint</c>, so callers cast at the boundary.
    /// </summary>
    public long SignCount { get; set; }

    public Guid AaGuid { get; set; }

    /// <summary>Comma-separated transport hints (e.g. "internal,hybrid"); null when unknown.</summary>
    [MaxLength(128)]
    public string? Transports { get; set; }

    /// <summary>User-facing label, e.g. "MacBook Touch ID".</summary>
    [Required, MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }
}
