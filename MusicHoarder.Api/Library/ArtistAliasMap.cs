using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// In-memory view of <see cref="ArtistAlias"/> rows: per-owner variant-key → canonical-spelling
/// mapping written by artist merges. The anti-oscillation contract: every code path that writes an
/// artist spelling from an external source (album-split heal's canonical overlay, enrichment's
/// provider match) must route the incoming name through this map, otherwise the next pass silently
/// reintroduces a merged-away variant and un-does the user's merge.
/// </summary>
public sealed class ArtistAliasMap
{
    public static readonly ArtistAliasMap Empty = new(new Dictionary<(Guid, string), string>());

    private readonly IReadOnlyDictionary<(Guid Owner, string Key), string> _aliases;

    private ArtistAliasMap(IReadOnlyDictionary<(Guid, string), string> aliases) => _aliases = aliases;

    public bool IsEmpty => _aliases.Count == 0;

    /// <summary>All owners' aliases — for cross-tenant sweeps (heals) that scope per group.</summary>
    public static async Task<ArtistAliasMap> LoadAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        var rows = await db.ArtistAliases
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(a => new { a.OwnerUserId, a.AliasKey, a.CanonicalName })
            .ToListAsync(ct);
        if (rows.Count == 0)
            return Empty;

        var map = new Dictionary<(Guid, string), string>();
        foreach (var r in rows)
            map[(r.OwnerUserId, r.AliasKey)] = r.CanonicalName;
        return new ArtistAliasMap(map);
    }

    /// <summary>One owner's aliases — for per-song flows that already know the owner.</summary>
    public static async Task<ArtistAliasMap> LoadForOwnerAsync(
        MusicHoarderDbContext db, Guid ownerUserId, CancellationToken ct)
    {
        var rows = await db.ArtistAliases
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId)
            .Select(a => new { a.AliasKey, a.CanonicalName })
            .ToListAsync(ct);
        if (rows.Count == 0)
            return Empty;

        var map = new Dictionary<(Guid, string), string>();
        foreach (var r in rows)
            map[(ownerUserId, r.AliasKey)] = r.CanonicalName;
        return new ArtistAliasMap(map);
    }

    /// <summary>
    /// Canonical spelling for a single artist name, or null when no alias covers it (or it already
    /// IS the canonical spelling).
    /// </summary>
    public string? ResolveName(Guid ownerUserId, string? name)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(name))
            return null;
        var key = TitleNormalizer.NormalizeForSearch(name);
        if (key.Length == 0 || !_aliases.TryGetValue((ownerUserId, key), out var canonical))
            return null;
        return string.Equals(canonical, name, StringComparison.Ordinal) ? null : canonical;
    }

    /// <summary>
    /// Maps a <c>;</c>-separated discrete artist list segment-by-segment. Segment COUNT is always
    /// preserved (rename only, no de-dup) so a positionally-aligned ArtistMusicBrainzIds list stays
    /// aligned. Returns null when nothing changed.
    /// </summary>
    public string? MapList(Guid ownerUserId, string? list)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(list))
            return null;

        var segments = MultiValue.Split(list);
        var changed = false;
        for (var i = 0; i < segments.Length; i++)
        {
            var canonical = ResolveName(ownerUserId, segments[i]);
            if (canonical is not null)
            {
                segments[i] = canonical;
                changed = true;
            }
        }
        return changed ? MultiValue.Join(segments) : null;
    }
}
