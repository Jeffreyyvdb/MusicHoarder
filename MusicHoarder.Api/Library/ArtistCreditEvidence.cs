using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// What the owner's library itself says about which names are several artists, built once per
/// call from every eligible row so detection, merge and split judge a name by the same evidence.
/// A credit is a COLLAB when (i) <see cref="ArtistCreditNormalizer.SplitArtists"/> splits it; (ii)
/// some row carries it as Artist or AlbumArtist while that row's own discrete list (≥2) is its
/// joined form — MusicBrainz's artist-credit split, which reads "Hef met Jayh", "2Pac + Outlawz"
/// and "50 Cent and Olivia" in any language; or (iii) it splits on an ambiguous joiner ("+",
/// "and", "met", "en", "vs") and EVERY part exists as an artist of its own — which keeps
/// "Florence + the Machine" one name.
/// </summary>
internal sealed class ArtistCreditEvidence
{
    // Artist keys of every one-artist name. Corroborates ambiguous joins: the parts come verbatim
    // from the credit's text, so a part must BE a standalone artist modulo case/diacritics/
    // punctuation. The search key would also let a qualified name ("The Machine (live)") vouch
    // for a bare part — too loose for a test whose false positive hides a real spelling cluster
    // and offers a split that rewrites the album artist.
    private readonly HashSet<string> _standaloneArtistKeys;

    // The same names by search key: the corroboration combined credits have always used for
    // SplitArtists' delimiters ("&", ",", " x ", feat.), kept so today's offers don't change.
    private readonly HashSet<string> _standaloneSearchKeys;

    // Artist key of a credit → the discrete list (most common, then ordinal) proving it joined.
    private readonly Dictionary<string, string[]> _joinedLists;

    // MusicBrainz artist id → artist keys it is aligned with in some row's discrete list.
    private readonly Dictionary<string, HashSet<string>> _idOwners;

    private ArtistCreditEvidence(
        List<string> singleArtistNames,
        Dictionary<string, string[]> joinedLists,
        Dictionary<string, HashSet<string>> idOwners)
    {
        SingleArtistNames = singleArtistNames;
        _standaloneArtistKeys = KeySet(singleArtistNames, TitleNormalizer.NormalizeArtistKey);
        _standaloneSearchKeys = KeySet(singleArtistNames, TitleNormalizer.NormalizeForSearch);
        _joinedLists = joinedLists;
        _idOwners = idOwners;
    }

    /// <summary>Names that SplitArtists leaves whole, minus placeholders and phantom delimiter
    /// segments. Collabs by (ii)/(iii) are still in here — filter with <see cref="IsCollabCredit"/>.</summary>
    public IReadOnlyList<string> SingleArtistNames { get; }

    public static ArtistCreditEvidence Build(IReadOnlyList<SongMetadata> songs, IEnumerable<string> names)
    {
        var single = names
            .Where(n => ArtistCreditNormalizer.SplitArtists(n).Count == 1)
            .Where(n => !IsPlaceholderName(n) && !ArtistCreditNormalizer.IsDelimiterToken(n))
            .ToList();

        var lists = new Dictionary<string, Dictionary<string, (int Count, string[] List)>>(StringComparer.Ordinal);
        var idOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var artistKeyCache = new Dictionary<string, string>(StringComparer.Ordinal);

        string ArtistKey(string value)
        {
            if (!artistKeyCache.TryGetValue(value, out var key))
            {
                key = TitleNormalizer.NormalizeArtistKey(value);
                artistKeyCache[value] = key;
            }
            return key;
        }

        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.Artists))
                continue;

            var raw = MultiValue.Split(song.Artists);
            if (raw.Length == 0)
                continue;

            var ids = MultiValue.Split(song.ArtistMusicBrainzIds);
            if (ids.Length == raw.Length)
            {
                for (var i = 0; i < raw.Length; i++)
                {
                    var owners = idOwners.TryGetValue(ids[i], out var o) ? o : idOwners[ids[i]] = new(StringComparer.Ordinal);
                    owners.Add(ArtistKey(raw[i]));
                }
            }

            var hasDelimiter = false;
            for (var i = 0; i < raw.Length; i++)
            {
                if (ArtistCreditNormalizer.IsDelimiterToken(raw[i]))
                {
                    hasDelimiter = true;
                    break;
                }
            }
            var discrete = hasDelimiter
                ? raw.Where(s => !ArtistCreditNormalizer.IsDelimiterToken(s)).ToArray()
                : raw;
            if (discrete.Length < 2)
                continue;

            var joined = MultiValue.Join(discrete)!;
            string? firstKey = null;
            if (!string.IsNullOrWhiteSpace(song.Artist) && ArtistCreditNormalizer.IsJoinedCreditOf(song.Artist, discrete))
            {
                firstKey = ArtistKey(song.Artist);
                var byList = lists.TryGetValue(firstKey, out var l) ? l : lists[firstKey] = new(StringComparer.Ordinal);
                byList[joined] = (byList.GetValueOrDefault(joined).Count + 1, discrete);
            }
            if (!string.IsNullOrWhiteSpace(song.AlbumArtist) && ArtistCreditNormalizer.IsJoinedCreditOf(song.AlbumArtist, discrete))
            {
                var albumKey = ArtistKey(song.AlbumArtist);
                if (!string.Equals(albumKey, firstKey, StringComparison.Ordinal))
                {
                    var byList = lists.TryGetValue(albumKey, out var l) ? l : lists[albumKey] = new(StringComparer.Ordinal);
                    byList[joined] = (byList.GetValueOrDefault(joined).Count + 1, discrete);
                }
            }
        }

        var joinedLists = lists.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .OrderByDescending(x => x.Value.Count)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .First().Value.List,
            StringComparer.Ordinal);
        return new ArtistCreditEvidence(single, joinedLists, idOwners);
    }

    /// <summary>True for any credit that names several artists by rules (i)–(iii) — never a
    /// spelling of one artist, so never clustered, merged or aliased.</summary>
    public bool IsCollabCredit(string name) =>
        ArtistCreditNormalizer.SplitArtists(name).Count > 1
        || IsProvenByADiscreteList(name)
        || CorroboratedJoin(name) is not null;

    /// <summary>A collab the library corroborates enough to OFFER splitting, and its parts: the
    /// proving discrete list; else SplitArtists' parts when an explicit featuring clause's lead
    /// — or, for "&amp;"/","/" x ", every part — exists standalone (Earth, Wind &amp; Fire stays
    /// one name); else a corroborated ambiguous join. Null for anything else.</summary>
    public IReadOnlyList<string>? OfferedParts(string credit)
    {
        if (_joinedLists.TryGetValue(TitleNormalizer.NormalizeArtistKey(credit), out var list))
            return list;
        var split = ArtistCreditNormalizer.SplitArtists(credit);
        if (split.Count > 1)
        {
            var required = ArtistCreditNormalizer.HasFeaturingDelimiter(credit) ? split.Take(1) : split;
            return required.All(p => _standaloneSearchKeys.Contains(TitleNormalizer.NormalizeForSearch(p)))
                ? split
                : null;
        }
        return CorroboratedJoin(credit);
    }

    /// <summary>A credit merge must refuse as its canonical: any featuring credit, or anything the
    /// combined-credit report would offer to split. A bare "&amp;" name the library can't
    /// corroborate ("Simon &amp; Garfunkel") stays a valid canonical.</summary>
    public bool IsCombinedCredit(string name) =>
        ArtistCreditNormalizer.HasFeaturingDelimiter(name) || OfferedParts(name) is not null;

    /// <summary>True when split has something to go on: parts from the credit's own text, or a
    /// row whose discrete list proves the join.</summary>
    public bool CanSplit(string credit) => TextParts(credit) is not null || IsProvenByADiscreteList(credit);

    /// <summary>
    /// The changes a split of <paramref name="credit"/> makes to <paramref name="rows"/>. Rows
    /// match by ARTIST key: under the search key a feat credit keys as its lead, so splitting
    /// "Kanye West feat. Kid Cudi" wrote that list onto every plain "Kanye West" row too. Parts per
    /// row: the row's own discrete list when it proves the join, else the credit's text parts.
    /// A blank Artists list is backfilled with the parts; an album artist equal to the credit
    /// moves to the lead, taking the lead's id when the row's ids are aligned, else keeping its
    /// id unless that id is provably a guest's.
    /// </summary>
    public List<SplitEdit> PlanSplit(string credit, IEnumerable<SongMetadata> rows)
    {
        var creditKey = TitleNormalizer.NormalizeArtistKey(credit);
        var textParts = TextParts(credit);
        var edits = new List<SplitEdit>();
        if (creditKey.Length == 0)
            return edits;

        foreach (var song in rows)
        {
            var artistIsCredit = TitleNormalizer.NormalizeArtistKey(song.Artist) == creditKey;
            var albumArtistIsCredit = TitleNormalizer.NormalizeArtistKey(song.AlbumArtist) == creditKey;
            if (!artistIsCredit && !albumArtistIsCredit)
                continue;

            var raw = MultiValue.Split(song.Artists);
            var discrete = DiscreteArtists(song);
            var rowProves = ArtistCreditNormalizer.IsJoinedCreditOf(credit, discrete);
            IReadOnlyList<string>? parts = rowProves ? discrete : textParts;
            if (parts is not { Count: > 1 })
                continue;

            var changes = new List<(string Field, string? Old, string? New)>();
            if (raw.Length == 0 && artistIsCredit)
                changes.Add((nameof(SongMetadata.Artists), song.Artists, MultiValue.Join(parts)));

            if (albumArtistIsCredit)
            {
                changes.Add((nameof(SongMetadata.AlbumArtist), song.AlbumArtist, parts[0]));
                var ids = MultiValue.Split(song.ArtistMusicBrainzIds);
                var leadId = rowProves && discrete.Length == raw.Length && ids.Length == raw.Length
                    ? ids[0]
                    : song.AlbumArtistMusicBrainzId is { } id && IsGuestId(id, parts)
                        ? null
                        : song.AlbumArtistMusicBrainzId;
                if (!string.Equals(leadId, song.AlbumArtistMusicBrainzId, StringComparison.Ordinal))
                    changes.Add((nameof(SongMetadata.AlbumArtistMusicBrainzId), song.AlbumArtistMusicBrainzId, leadId));
            }

            if (changes.Count > 0)
                edits.Add(new SplitEdit(song, changes, albumArtistIsCredit));
        }
        return edits;
    }

    private bool IsProvenByADiscreteList(string credit) =>
        _joinedLists.ContainsKey(TitleNormalizer.NormalizeArtistKey(credit));

    // Parts from the credit's text alone: SplitArtists' delimiters as the split action has always
    // taken them, else an ambiguous join only when every part is corroborated.
    private IReadOnlyList<string>? TextParts(string credit)
    {
        var split = ArtistCreditNormalizer.SplitArtists(credit);
        return split.Count > 1 ? split : CorroboratedJoin(credit);
    }

    private IReadOnlyList<string>? CorroboratedJoin(string credit)
    {
        var parts = ArtistCreditNormalizer.SplitOnAmbiguousJoiners(credit);
        return parts.Count > 1 && parts.All(p => _standaloneArtistKeys.Contains(TitleNormalizer.NormalizeArtistKey(p)))
            ? parts
            : null;
    }

    // An id is provably a guest's when aligned lists pair it with a non-lead part and never with
    // the lead — the stray-id case (an elected album artist keeping the previous row's id).
    private bool IsGuestId(string id, IReadOnlyList<string> parts)
    {
        if (!_idOwners.TryGetValue(id.Trim(), out var owners))
            return false;
        return !owners.Contains(TitleNormalizer.NormalizeArtistKey(parts[0]))
            && parts.Skip(1).Any(p => owners.Contains(TitleNormalizer.NormalizeArtistKey(p)));
    }

    private static HashSet<string> KeySet(IEnumerable<string> names, Func<string, string> key) =>
        names.Select(key).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>A row's discrete Artists list minus lone "feat."/"with" segments — the phantom artist
    /// the old capturing split wrote into ARTISTS, which must never count as an artist or as proof
    /// that a credit is joined.</summary>
    internal static string[] DiscreteArtists(SongMetadata song) =>
        MultiValue.Split(song.Artists).Where(s => !ArtistCreditNormalizer.IsDelimiterToken(s)).ToArray();

    /// <summary>One song a split would change, and the (field, old, new) changes it would make.</summary>
    internal sealed record SplitEdit(
        SongMetadata Song, IReadOnlyList<(string Field, string? Old, string? New)> Changes, bool MovesAlbumArtist);

    /// <summary>
    /// Placeholders on top of the various-artists sentinels
    /// (<see cref="DestinationPathResolver.IsVariousArtistsSentinel"/>, which owns those spellings
    /// so routing, grouping and dedup can never disagree about what counts as one).
    /// </summary>
    private static readonly HashSet<string> PlaceholderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unknown Artist", "Unknown", "[unknown]",
    };

    /// <summary>
    /// "Various Artists" and friends are a slot, not an artist. They occur under every album artist
    /// in the library and so accumulate every artist's MusicBrainz id — clustering or merging one is
    /// never right.
    /// </summary>
    internal static bool IsPlaceholderName(string name)
    {
        var trimmed = name.Trim();
        return DestinationPathResolver.IsVariousArtistsSentinel(trimmed) || PlaceholderNames.Contains(trimmed);
    }
}
