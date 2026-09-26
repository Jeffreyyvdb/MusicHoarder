using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sharing;

/// <summary>
/// Mints share links. One place, because two things make them: "Share link…" (<c>POST /api/shares</c>)
/// and sending a song or album in a chat, which travels as the same kind of link so the recipient
/// can play it whatever their access to the sender's library.
/// </summary>
public static class SongShareFactory
{
    /// <summary>
    /// The owner's active link for (song, scope), or a new one. Re-sharing hands back the same link
    /// instead of minting token sprawl. The caller has already checked that
    /// <paramref name="ownerUserId"/> owns <paramref name="songId"/>.
    /// </summary>
    public static async Task<SongShare> GetOrCreateAsync(
        MusicHoarderDbContext db,
        Guid ownerUserId,
        int songId,
        ShareScope scope,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var existing = await db.SongShares.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId
                && s.SongId == songId
                && s.Scope == scope
                && s.RevokedAtUtc == null, ct);
        if (existing is not null)
            return existing;

        var share = new SongShare
        {
            OwnerUserId = ownerUserId,
            SongId = songId,
            Token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            Scope = scope,
            CreatedAtUtc = nowUtc,
        };
        db.SongShares.Add(share);
        await db.SaveChangesAsync(ct);
        return share;
    }

    public static ShareScope ParseScope(string? scope) =>
        string.Equals(scope, "album", StringComparison.OrdinalIgnoreCase) ? ShareScope.Album : ShareScope.Song;
}
