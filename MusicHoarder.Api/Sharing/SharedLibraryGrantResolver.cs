using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sharing;

/// <summary>The caller's active grants from one owner, collapsed for membership evaluation.</summary>
public sealed record GrantSet(Guid OwnerUserId, bool Library, IReadOnlyList<GrantKey> Keys);

/// <summary>One album/artist grant's normalized keys. <see cref="AlbumKey"/> null = whole artist.</summary>
public sealed record GrantKey(string ArtistKey, string? AlbumKey);

public interface ISharedLibraryGrantResolver
{
    /// <summary>The caller's active grants, grouped per granting owner.</summary>
    Task<IReadOnlyList<GrantSet>> ResolveAsync(MusicHoarderDbContext db, Guid granteeId, CancellationToken ct);

    /// <summary>
    /// Every song one owner's grant set exposes. Bypasses the tenancy filter and re-scopes to the
    /// grant's own <see cref="GrantSet.OwnerUserId"/> — the same posture as the anonymous share
    /// endpoints — so a grant can never reach beyond the granting owner's rows.
    /// </summary>
    IQueryable<SongMetadata> ScopeSongs(MusicHoarderDbContext db, GrantSet set);

    /// <summary>Point lookup for stream/cover/lyrics: the song iff the caller's grants expose it.</summary>
    Task<SongMetadata?> ResolveSongAsync(MusicHoarderDbContext db, Guid granteeId, int songId, CancellationToken ct);
}

/// <summary>
/// Resolves what a friend may read. Membership semantics are copied from
/// <c>SharesEndpoints.LoadSongsInScopeAsync</c> so an album grant matches exactly what an
/// anonymous album share matches: lowercased <c>(AlbumArtist ?? Artist, Album)</c>, year
/// deliberately ignored. Library grants additionally require <c>LibraryBuildStatus == Done</c> —
/// the friend gets the built library, not half-enriched inbox rows. Duplicates and deleted rows
/// are never exposed.
/// </summary>
public sealed class SharedLibraryGrantResolver : ISharedLibraryGrantResolver
{
    public async Task<IReadOnlyList<GrantSet>> ResolveAsync(MusicHoarderDbContext db, Guid granteeId, CancellationToken ct)
    {
        // Explicit GranteeUserId predicate on top of the ambient query filter (which already
        // limits to rows where the caller is owner or grantee): an owner also sees the grants
        // they handed out, and must not have their own library counted as "shared with me".
        var grants = await db.LibraryShareGrants.AsNoTracking()
            .Where(g => g.GranteeUserId == granteeId && g.RevokedAtUtc == null)
            .ToListAsync(ct);

        return grants
            .GroupBy(g => g.OwnerUserId)
            .Select(group => new GrantSet(
                group.Key,
                Library: group.Any(g => g.Scope == ShareGrantScope.Library),
                Keys: group
                    .Where(g => g.Scope != ShareGrantScope.Library && g.ArtistKey is not null)
                    .Select(g => new GrantKey(g.ArtistKey!, g.Scope == ShareGrantScope.Album ? g.AlbumKey : null))
                    .ToList()))
            .ToList();
    }

    public IQueryable<SongMetadata> ScopeSongs(MusicHoarderDbContext db, GrantSet set)
    {
        var baseQuery = db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OwnerUserId == set.OwnerUserId
                && s.DeletedAtUtc == null
                && !s.IsDuplicate
                && !s.IsSynthetic);

        // Composed as UNIONs (one branch per grant kind) rather than a dynamic OR expression —
        // grant counts are small and EF translates Union cleanly on both Npgsql and InMemory.
        IQueryable<SongMetadata>? result = null;

        if (set.Library)
            result = baseQuery.Where(s => s.LibraryBuildStatus == LibraryBuildStatus.Done);

        var artistKeys = set.Keys.Where(k => k.AlbumKey is null).Select(k => k.ArtistKey).Distinct().ToList();
        if (artistKeys.Count > 0)
        {
            var part = baseQuery.Where(s => artistKeys.Contains(((s.AlbumArtist ?? s.Artist) ?? "").ToLower()));
            result = result is null ? part : result.Union(part);
        }

        // Album grants match as (artist, album) pairs — one branch per grant, so two same-named
        // albums by different artists can never cross-match.
        foreach (var key in set.Keys.Where(k => k.AlbumKey is not null))
        {
            var artistKey = key.ArtistKey;
            var albumKey = key.AlbumKey!;
            var part = baseQuery.Where(s => s.Album != null
                && s.Album.ToLower() == albumKey
                && ((s.AlbumArtist ?? s.Artist) ?? "").ToLower() == artistKey);
            result = result is null ? part : result.Union(part);
        }

        return result ?? baseQuery.Where(s => false);
    }

    public async Task<SongMetadata?> ResolveSongAsync(MusicHoarderDbContext db, Guid granteeId, int songId, CancellationToken ct)
    {
        var sets = await ResolveAsync(db, granteeId, ct);
        if (sets.Count == 0) return null;

        var song = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == songId
                && s.DeletedAtUtc == null
                && !s.IsDuplicate
                && !s.IsSynthetic, ct);
        if (song is null) return null;

        var set = sets.FirstOrDefault(x => x.OwnerUserId == song.OwnerUserId);
        return set is not null && Matches(set, song) ? song : null;
    }

    /// <summary>In-memory twin of <see cref="ScopeSongs"/>'s predicate for point lookups.</summary>
    internal static bool Matches(GrantSet set, SongMetadata song)
    {
        if (set.Library && song.LibraryBuildStatus == LibraryBuildStatus.Done)
            return true;

        var artistKey = ((song.AlbumArtist ?? song.Artist) ?? "").ToLower();
        var albumKey = song.Album?.ToLower();

        foreach (var key in set.Keys)
        {
            if (key.ArtistKey != artistKey) continue;
            if (key.AlbumKey is null) return true;
            if (albumKey is not null && key.AlbumKey == albumKey) return true;
        }
        return false;
    }
}
