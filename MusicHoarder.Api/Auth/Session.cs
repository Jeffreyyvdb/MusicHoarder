using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// Server-side session row. The signed cookie carries only <see cref="Id"/>; the row is the
/// source of truth (revocable via <see cref="RevokedAtUtc"/>, expirable via
/// <see cref="ExpiresAtUtc"/>).
/// </summary>
public class Session
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    public bool IsActive(DateTime nowUtc) =>
        RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}
