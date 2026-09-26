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
/// <para>
/// An alias maps one artist's spellings, so it never rewrites a credit naming several artists.
/// Lookups use <see cref="TitleNormalizer.NormalizeArtistKey"/>, the key merges now write. Rows
/// written under the older search key are the same string for every plain name, so they keep
/// resolving; there is deliberately NO search-key fallback for the rest. That form strips
/// parenthetical and featuring text — looking names up by it is how the "nas" row every merge onto
/// "Nas" stores rewrote "Nas (feat. Lauryn Hill)" to "Nas". Once multi-artist credits are refused,
/// all a fallback could still reach is a qualified single name ("Future (rapper)"): providers name
/// artists without such qualifiers, so an old alias that no longer matches one costs at most a
/// re-merge, while applying it would erase the qualifier from rows nobody merged.
/// </para>
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
    /// Canonical spelling for a single artist name, or null when no alias covers it, it already IS
    /// the canonical spelling, or it credits several artists. <paramref name="discreteArtists"/> is
    /// the stored ';' list of the same match or row, when there is one: a name that is its joined
    /// form ("Hef met Jayh" over "Hef; Jayh") is several artists in any language.
    /// </summary>
    public string? ResolveName(Guid ownerUserId, string? name, string? discreteArtists = null)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(name))
            return null;

        // Multi-artist guard BEFORE any lookup: a featuring clause, "&", "," or " x " credit — or one
        // its own discrete list shows to be joined — is several artists, and rewriting it whole to
        // an alias's canonical deletes every other artist in it.
        if (ArtistCreditNormalizer.SplitArtists(name).Count > 1
            || ArtistCreditNormalizer.IsJoinedCreditOf(name, MultiValue.Split(discreteArtists)))
            return null;

        var key = TitleNormalizer.NormalizeArtistKey(name);
        if (key.Length == 0 || !_aliases.TryGetValue((ownerUserId, key), out var canonical))
            return null;
        if (string.Equals(canonical, name, StringComparison.Ordinal))
            return null;

        // An alias that swaps a credit for one of its own parts, or a name for a credit containing
        // it, is not a spelling. New merges refuse both, but rows from before that guard exist
        // ("hef met jayh" → "Hef", "nas" → "Nas (featuring AZ)"), and applying one would strip, or
        // add, artists on every heal and re-enrichment.
        var canonicalKey = TitleNormalizer.NormalizeArtistKey(canonical);
        if (!string.Equals(canonicalKey, key, StringComparison.Ordinal)
            && (JoinsAsPart(name, canonicalKey) || JoinsAsPart(canonical, key)))
            return null;

        return canonical;
    }

    /// <summary>
    /// True when <paramref name="credit"/> reads as several artists — by
    /// <see cref="ArtistCreditNormalizer.SplitArtists"/>, else on an ambiguous joiner ("+", "and",
    /// "met"…) — and one of those parts has the artist key <paramref name="partKey"/>. Structural
    /// only (no library evidence): "Hef met Jayh" joins "hef" as a part, "Kanye West" does not join
    /// "kanye". A name and a credit containing it are never two spellings of one artist.
    /// </summary>
    internal static bool JoinsAsPart(string credit, string partKey)
    {
        if (partKey.Length == 0)
            return false;
        var parts = ArtistCreditNormalizer.SplitArtists(credit);
        if (parts.Count < 2)
            parts = ArtistCreditNormalizer.SplitOnAmbiguousJoiners(credit);
        return parts.Count > 1
            && parts.Any(p => string.Equals(TitleNormalizer.NormalizeArtistKey(p), partKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// Maps a <c>;</c>-separated discrete artist list segment-by-segment through
    /// <see cref="ResolveName"/>, so a segment that is itself several artists stays as it is.
    /// Segment COUNT is always preserved (rename only, no de-dup) so a positionally-aligned
    /// ArtistMusicBrainzIds list stays aligned. Returns null when nothing changed.
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
