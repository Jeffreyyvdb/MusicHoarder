using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// A login identity. Invite-only — rows are created by EF <c>HasData</c> at migration time
/// (Owner + Demo) and never inserted at runtime. <see cref="EmailNormalized"/> is the unique
/// lookup key; <see cref="Email"/> is preserved for display.
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

    public bool IsDisabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastLoginAtUtc { get; set; }

    public static string Normalize(string email) =>
        email?.Trim().ToUpperInvariant() ?? string.Empty;
}
