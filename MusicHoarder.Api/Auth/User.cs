using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// A login identity. Invite-only — the Admin and Demo rows are created by EF <c>HasData</c> at
/// migration time; <see cref="UserRole.Member"/> rows are the one runtime insert path, created
/// when an admin-minted <see cref="Invite"/> is accepted. <see cref="EmailNormalized"/> is the
/// unique lookup key; <see cref="Email"/> is preserved for display.
/// </summary>
public class User
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Uppercase-invariant form of <see cref="Email"/>. Unique.</summary>
    [Required, MaxLength(256)]
    public string EmailNormalized { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? DisplayName { get; set; }

    public UserRole Role { get; set; }

    /// <summary>
    /// What this account may do, as granted by an admin. Do not read this directly to authorize —
    /// go through <see cref="CurrentUser.Can"/>, which folds in the rule that an
    /// <see cref="UserRole.Admin"/> implicitly holds every flag.
    /// </summary>
    public Capability Capabilities { get; set; }

    public bool IsDisabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastLoginAtUtc { get; set; }

    public static string Normalize(string email) =>
        email?.Trim().ToUpperInvariant() ?? string.Empty;
}
