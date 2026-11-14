using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Encrypts credential columns that were written before <see cref="ISecretColumnProtector"/>
/// existed. The converter reads such a value unchanged, but EF sees no change on a row it only
/// read, so a refresh token or client secret nobody rewrites would stay plain text forever.
/// Runs at startup; a no-op once every row carries the prefix.
/// </summary>
public static class LegacySecretColumns
{
    /// <returns>The number of rows that were rewritten.</returns>
    public static async Task<int> ProtectAsync(
        DbContextOptions options,
        ICurrentUserAccessor currentUser,
        ISecretColumnProtector protector,
        CancellationToken ct = default)
    {
        // A context without the protector sees the stored text, prefix and all.
        List<int> legacyIds;
        await using (var raw = new MusicHoarderDbContext(options))
        {
            legacyIds = await raw.SpotifySettings
                .IgnoreQueryFilters()
                .Where(s =>
                    (s.ClientSecret != null && !s.ClientSecret.StartsWith(SecretColumnProtector.Prefix))
                    || (s.AccessToken != null && !s.AccessToken.StartsWith(SecretColumnProtector.Prefix))
                    || (s.RefreshToken != null && !s.RefreshToken.StartsWith(SecretColumnProtector.Prefix)))
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        if (legacyIds.Count == 0)
            return 0;

        await using var db = new MusicHoarderDbContext(options, currentUser, protector);
        var rows = await db.SpotifySettings
            .IgnoreQueryFilters()
            .Where(s => legacyIds.Contains(s.Id))
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            var entry = db.Entry(row);
            entry.Property(s => s.ClientSecret).IsModified = true;
            entry.Property(s => s.AccessToken).IsModified = true;
            entry.Property(s => s.RefreshToken).IsModified = true;
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
