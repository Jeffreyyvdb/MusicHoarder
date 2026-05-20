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
