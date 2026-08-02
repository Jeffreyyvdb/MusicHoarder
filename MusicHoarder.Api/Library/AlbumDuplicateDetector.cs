using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>Two album titles under one artist that likely refer to the same album.</summary>
public sealed record AlbumDuplicatePair(
    string ArtistKey,
    string ArtistDisplay,
    string AlbumA,
    int SongCountA,
    string AlbumB,
    int SongCountB,
    double? FuzzyRatio,
    string Evidence);

public interface IAlbumDuplicateDetector
{
    /// <summary>Near-duplicate album pairs for one owner — titles <see cref="AlbumGroupKey"/> keeps
    /// apart but that normalize/fuzzy-match together. Read-only; respects dismissals.</summary>
    Task<IReadOnlyList<AlbumDuplicatePair>> DetectAsync(Guid ownerUserId, CancellationToken ct = default);
}

/// <summary>
/// Complements <see cref="AlbumSplitHealer"/>: the healer converges groups that already share an
/// <see cref="AlbumGroupKey"/>, while this finds pairs the exact-normalized key MISSES — "The
/// Blueprint 3" vs "Blueprint 3" (leading "the"), "B &amp; C" vs "B and C", and near-miss
/// spellings. Under-merge is the safe direction: pairs must carry identical
/// <see cref="VersionQualifier"/> edition flags, so deluxe never suggests merging into standard.
/// </summary>
public sealed class AlbumDuplicateDetector(
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options) : IAlbumDuplicateDetector
{
    public async Task<IReadOnlyList<AlbumDuplicatePair>> DetectAsync(
        Guid ownerUserId, CancellationToken ct = default)
    {
        var songs = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .ExcludingDemoTenant()
            .Where(s => s.Album != null && s.Album != "")
            .ToListAsync(ct);

        var dismissed = await db.DedupDismissals
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.OwnerUserId == ownerUserId && d.Kind == DedupDismissalKind.AlbumPair)
            .Select(d => new { d.ScopeKey, d.KeyLow, d.KeyHigh })
            .ToListAsync(ct);
        var dismissedPairs = dismissed.Select(d => (d.ScopeKey, d.KeyLow, d.KeyHigh)).ToHashSet();

        // One entry per logical album (AlbumGroupKey), carrying its majority display spelling.
        var albums = songs
            .Select(s => (Key: AlbumGroupKey.For(s), Song: s))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!)
            .Select(g =>
            {
                var members = g.Select(x => x.Song).ToList();
                var display = members
                    .GroupBy(s => s.Album!, StringComparer.Ordinal)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .First().Key;
                var artistDisplay = members
                    .Select(s => s.AlbumArtist ?? s.Artist)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .GroupBy(a => a!, StringComparer.Ordinal)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => x.Key)
                    .FirstOrDefault() ?? string.Empty;
                return new AlbumEntry(
                    g.Key.ArtistKey,
                    artistDisplay,
                    display,
                    TitleNormalizer.NormalizeForSearch(display),
                    // Detect on the TITLE argument: the album-argument path masks to packaging
                    // flags only, which would let "X: Live from ..." pair with the studio "X".
                    VersionQualifier.Detect(display),
                    members.Count);
            })
            .ToList();

        var threshold = options.Value.AlbumMergeFuzzyThreshold;
        var pairs = new List<AlbumDuplicatePair>();

        foreach (var artistGroup in albums.GroupBy(a => a.ArtistKey, StringComparer.Ordinal))
        {
            var entries = artistGroup.OrderBy(a => a.Display, StringComparer.Ordinal).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                for (var j = i + 1; j < entries.Count; j++)
                {
                    var (a, b) = (entries[i], entries[j]);

                    // Identical edition flags only — deluxe/live/remaster never merges into plain.
                    if (a.Qualifiers != b.Qualifiers)
                        continue;
                    // Same search key = same AlbumGroupKey — that's AlbumSplitHealer territory.
                    if (a.SearchKey == b.SearchKey)
                        continue;
                    if (IsDismissed(dismissedPairs, artistGroup.Key, a.SearchKey, b.SearchKey))
                        continue;
                    // Differing numbering is identity, not spelling: "Yeezus" vs "Yeezus 2" and
                    // sequel/edition titles must never fuzzy-pair no matter how similar the text.
                    if (!NumbersMatch(a.Display, b.Display))
                        continue;

                    string evidence;
                    double? ratio = null;
                    if (TitleNormalizer.NormalizeForDedup(a.Display) == TitleNormalizer.NormalizeForDedup(b.Display)
                        && TitleNormalizer.NormalizeForDedup(a.Display).Length > 0)
                    {
                        evidence = "same title after normalization";
                    }
                    else
                    {
                        // Fuzzy on short titles is noise ("ye" partial-matches anything containing
                        // it); the exact-normalized path above still catches short-title variants.
                        if (a.SearchKey.Length < 4 || b.SearchKey.Length < 4)
                            continue;
                        ratio = FuzzyTextMatch.Ratio(a.Display, b.Display);
                        if ((ratio ?? 0) < threshold)
                            continue;
                        evidence = "similar title";
                    }

                    pairs.Add(new AlbumDuplicatePair(
                        artistGroup.Key, a.ArtistDisplay.Length > 0 ? a.ArtistDisplay : b.ArtistDisplay,
                        a.Display, a.SongCount, b.Display, b.SongCount, ratio, evidence));
                }
            }
        }

        return pairs
            .OrderByDescending(p => p.SongCountA + p.SongCountB)
            .ThenBy(p => p.AlbumA, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Digit runs of both dedup-normalized titles must agree (order-sensitive) —
    /// bracketed edition markers like "[V2]" are already stripped by the normalizer.</summary>
    private static bool NumbersMatch(string displayA, string displayB)
    {
        static List<string> Numbers(string display) => System.Text.RegularExpressions.Regex
            .Matches(TitleNormalizer.NormalizeForDedup(display), @"\d+")
            .Select(m => m.Value.TrimStart('0'))
            .ToList();
        return Numbers(displayA).SequenceEqual(Numbers(displayB), StringComparer.Ordinal);
    }

    private static bool IsDismissed(
        HashSet<(string Scope, string Low, string High)> dismissed, string artistKey, string keyA, string keyB)
    {
        var (low, high) = string.CompareOrdinal(keyA, keyB) <= 0 ? (keyA, keyB) : (keyB, keyA);
        return dismissed.Contains((artistKey, low, high));
    }

    private sealed record AlbumEntry(
        string ArtistKey,
        string ArtistDisplay,
        string Display,
        string SearchKey,
        VersionQualifiers Qualifiers,
        int SongCount);
}
