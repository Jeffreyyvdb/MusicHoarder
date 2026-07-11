using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public enum ShareScope
{
    Song = 0,
    Album = 1,
}

/// <summary>
/// A capability link the owner hands to a friend: whoever holds the token can play the shared
/// song (or its whole album), and read its lyrics and basic metadata — nothing else. Unlike
/// <see cref="Auth.MagicLinkToken"/> the token is stored in plaintext: it must be re-shown and
/// re-copied from the app after creation, and it grants access to one song's audio, not to the
/// account. Revoking sets <see cref="RevokedAtUtc"/>; the link then 404s.
/// </summary>
public class SongShare
{
    public int Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public int SongId { get; set; }

    public SongMetadata? Song { get; set; }

    /// <summary>URL-safe random token (128 bits, base64url) — the whole capability.</summary>
    [MaxLength(64)]
    public required string Token { get; set; }

    public ShareScope Scope { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public bool IsActive => RevokedAtUtc is null;
}
