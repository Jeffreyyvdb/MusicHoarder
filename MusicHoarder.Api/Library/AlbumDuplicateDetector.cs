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

    /// <summary>
    /// How many pairs <see cref="DetectAsync"/> would report for this owner, from songs the caller
    /// has already read for several detectors at once (see <see cref="InboxNameCounter"/>):
    /// <paramref name="library"/> is every song that is not deleted, synthetic or the demo account's,
    /// in id order, carrying at least <see cref="AlbumDuplicateDetector.CountColumns"/>. The count is
    /// remembered for as long as the names, the dismissals and the threshold it came from stay the same.
    /// </summary>
    Task<int> CountAsync(Guid ownerUserId, IReadOnlyList<SongMetadata> library, CancellationToken ct = default);
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
    IOptions<MusicEnricherOptions> options,
    DetectionCountMemo memo) : IAlbumDuplicateDetector
{
    /// <summary>
    /// Every song column <see cref="Detect"/> reads: the album title and the fields
    /// <see cref="AlbumGroupKey.For"/> keys an album on. A name compare over the whole library has no
    /// use for the other hundred, and nothing else is loaded — so a property read here that is not
    /// in this list is always blank.
    /// </summary>
    internal static readonly SongColumnSet DetectColumns = new(
        nameof(SongMetadata.OwnerUserId),
        nameof(SongMetadata.Album),
        nameof(SongMetadata.AlbumArtist),
        nameof(SongMetadata.Artist),
        nameof(SongMetadata.IsCompilation));

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
            .WithOnly(db, DetectColumns)
            .ToListAsync(ct);
        var dismissedPairs = await LoadDismissedPairsAsync(ownerUserId, ct);

        return Detect(songs, dismissedPairs, options.Value.AlbumMergeFuzzyThreshold);
    }

    /// <summary>
    /// What <see cref="CountAsync"/> needs on a row it did not load itself: the columns detection
    /// reads, and the one the rest of <see cref="DetectAsync"/>'s filter tests.
    /// </summary>
    internal static readonly SongColumnSet CountColumns = DetectColumns.With(nameof(SongMetadata.IsDuplicate));

    public async Task<int> CountAsync(
        Guid ownerUserId, IReadOnlyList<SongMetadata> library, CancellationToken ct = default)
    {
        // The part of DetectAsync's filter the shared read has not applied.
        var songs = library
            .Where(s => s.OwnerUserId == ownerUserId && !s.IsDuplicate && !string.IsNullOrEmpty(s.Album))
            .ToList();
        var dismissedPairs = await LoadDismissedPairsAsync(ownerUserId, ct);
        var threshold = options.Value.AlbumMergeFuzzyThreshold;

        // The stamp and the detection take the same three arguments. Detect gets copies that carry
        // DetectColumns only — these rows hold other detectors' columns too, and a column the stamp
        // does not cover must be one Detect cannot see.
        return memo.GetOrCount(
            nameof(AlbumDuplicateDetector),
            ownerUserId,
            StampOf(songs, dismissedPairs, threshold),
            () => Detect(DetectColumns.Copy(songs), dismissedPairs, threshold).Count);
    }

    /// <summary>
    /// A fingerprint of everything <see cref="Detect"/> is given, parameter for parameter: its
    /// columns of its songs in order, the dismissals (which it only looks up, so in a fixed order)
    /// and the threshold. A parameter added to one must be added to the other.
    /// </summary>
    internal static byte[] StampOf(
        IReadOnlyList<SongMetadata> songs,
        HashSet<(string Scope, string Low, string High)> dismissedPairs,
        double threshold)
    {
        using var stamp = new ContentStamp();
        DetectColumns.AddTo(stamp, songs);
        stamp.Add(dismissedPairs.Count);
        foreach (var (scope, low, high) in dismissedPairs
            .OrderBy(p => p.Scope, StringComparer.Ordinal)
            .ThenBy(p => p.Low, StringComparer.Ordinal)
            .ThenBy(p => p.High, StringComparer.Ordinal))
        {
            stamp.Add(scope);
            stamp.Add(low);
            stamp.Add(high);
        }
        stamp.Add(threshold);
        return stamp.Finish();
    }

    private async Task<HashSet<(string Scope, string Low, string High)>> LoadDismissedPairsAsync(
        Guid ownerUserId, CancellationToken ct)
    {
        var dismissed = await db.DedupDismissals
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.OwnerUserId == ownerUserId && d.Kind == DedupDismissalKind.AlbumPair)
            .Select(d => new { d.ScopeKey, d.KeyLow, d.KeyHigh })
            .ToListAsync(ct);
        return dismissed.Select(d => (d.ScopeKey, d.KeyLow, d.KeyHigh)).ToHashSet();
    }

    /// <summary>
    /// The detection itself, over songs already loaded: one owner's rows that carry an album title.
    /// Static so that the rows, the dismissals and the threshold are all it can see.
    /// </summary>
    internal static IReadOnlyList<AlbumDuplicatePair> Detect(
        IReadOnlyList<SongMetadata> songs,
        HashSet<(string Scope, string Low, string High)> dismissedPairs,
        double threshold)
    {
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
