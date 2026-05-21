using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Auth;

public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Email of the owner (admin) user. Set via AppHost parameter <c>owner-email</c>. The owner's
    /// row is created at migration time with a placeholder; the seeder updates it from this value
    /// on every boot, so changing your email here propagates without a migration.
    /// </summary>
    [Required, EmailAddress]
    public string OwnerEmail { get; set; } = string.Empty;

    /// <summary>Email of the demo user. Same propagation story as <see cref="OwnerEmail"/>.</summary>
    [Required, EmailAddress]
    public string DemoUserEmail { get; set; } = "demo@musichoarder.local";

    /// <summary>
    /// Optional pre-registered owner passkey, seeded into a fresh database on boot so environments
    /// that start empty (per-PR previews) accept the owner's existing passkey without a new
    /// registration ceremony. JSON object with <c>credentialId</c> + <c>publicKey</c> base64, plus
    /// <c>aaGuid</c>, <c>signCount</c>, <c>transports</c>, <c>displayName</c>. Public-key material
    /// only (the private key never leaves the authenticator), so it is safe to inject. Empty →
    /// skipped. Only valid when the relying-party id matches the one the passkey was registered
    /// against — previews pin <see cref="WebAuthnOptions.RpId"/> to the shared parent domain so a
    /// single registration is reusable across every <c>pr-N</c> subdomain.
    /// </summary>
    public string OwnerSeedCredentialJson { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int MagicLinkTtlMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int SessionLifetimeDays { get; set; } = 30;

    [MaxLength(64)]
    public string CookieName { get; set; } = "mh_session";

    /// <summary>
    /// Path containing data-protection keys. Must be writable and persisted across restarts
    /// or every cookie invalidates on reboot. Backed by a docker volume in production.
    /// </summary>
    [Required]
    public string DataProtectionKeysPath { get; set; } = "/data/dpkeys";
}
