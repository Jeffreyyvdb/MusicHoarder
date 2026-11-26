using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>One artist-name spelling with how often it occurs in the owner's library.</summary>
public sealed record ArtistNameStat(string Name, int SongCount, IReadOnlyList<string> MusicBrainzIds);

/// <summary>A cluster of spellings that likely refer to the same artist.</summary>
public sealed record ArtistDuplicateCluster(
    string SuggestedCanonical,
    IReadOnlyList<ArtistNameStat> Variants,
    IReadOnlyList<string> Evidence);

/// <summary>
/// A credit that names several artists but is registered as one: the track credit of songs that
/// carry no discrete Artists list ("A &amp; B" — the split-artist defect the artists grid inherits),
/// or the ALBUM artist of songs ("Hef met Jayh" — which files them in a folder of their own, apart
/// from the lead's albums). <see cref="SongCount"/> is the distinct songs a split changes;
/// <see cref="AlbumArtistSongCount"/> how many of those it re-files under the lead,
/// <c>Parts[0]</c>, because the credit is their album artist.
/// </summary>
public sealed record CombinedCreditCandidate(
    string Credit, IReadOnlyList<string> Parts, int SongCount, int AlbumArtistSongCount = 0);

public sealed record ArtistDuplicateReport(
    IReadOnlyList<ArtistDuplicateCluster> Clusters,
    IReadOnlyList<CombinedCreditCandidate> CombinedCredits);

public sealed record ArtistMergeResult(int SongsUpdated, int SongsRequeued, int AliasesStored);

public sealed record ArtistSplitCreditResult(int SongsUpdated, int SongsRequeued);

public interface IArtistDuplicateService
{
    /// <summary>Detects likely-duplicate artist spellings and combined-credit candidates for one
    /// owner. Read-only; respects prior dismissals.</summary>
    Task<ArtistDuplicateReport> DetectAsync(Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// How many clusters and combined credits <see cref="DetectAsync"/> would report for this owner,
    /// together, from songs the caller has already read for several detectors at once (see
    /// <see cref="InboxNameCounter"/>): <paramref name="library"/> is every song that is not
    /// deleted, synthetic or the demo account's, in id order, carrying at least
    /// <see cref="ArtistDuplicateService.CountColumns"/>. The count is remembered for as long as the
    /// names, the dismissals and the settings it came from stay the same.
    /// </summary>
    Task<int> CountAsync(Guid ownerUserId, IReadOnlyList<SongMetadata> library, CancellationToken ct = default);

    /// <summary>
    /// Rewrites every whole-field occurrence of the variant spellings (Artist / AlbumArtist) and
    /// every matching segment of the discrete Artists list to the canonical spelling, audits each
    /// change, re-queues built rows for re-tag, and stores <see cref="ArtistAlias"/> rows so heals
    /// and re-enrichment can't reintroduce the variants. Matching is by
    /// <see cref="TitleNormalizer.NormalizeArtistKey"/>, so only re-spellings of the SAME name are
    /// touched; collab credits ("A &amp; B", "Hef met Jayh", "Nas (featuring AZ)") are never
    /// rewritten or aliased, even when passed as a variant, and are refused as the canonical.
    /// </summary>
    Task<ArtistMergeResult> MergeAsync(
        Guid ownerUserId, string canonicalName, IReadOnlyList<string> variantNames, CancellationToken ct = default);

    /// <summary>For songs whose credit is the given combined credit (by artist key): backfills a
    /// blank discrete Artists list with its parts, and moves an album artist equal to the credit to
    /// the lead part. Throws <see cref="ArgumentException"/> when the credit can't be shown to name
    /// several artists.</summary>
    Task<ArtistSplitCreditResult> SplitCreditAsync(Guid ownerUserId, string creditName, CancellationToken ct = default);

    /// <summary>Records "these are not the same artist" for every pair among the given names.</summary>
    Task<int> DismissAsync(Guid ownerUserId, IReadOnlyList<string> names, CancellationToken ct = default);
}

public sealed class ArtistDuplicateService(
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options,
    DetectionCountMemo memo,
    ILogger<ArtistDuplicateService> logger) : IArtistDuplicateService
{
    /// <summary>
    /// Every song column <see cref="Detect"/> reads: the three places a row names an artist and the
    /// MusicBrainz ids that travel with them. Nothing else is loaded for detection — so a property
    /// read there (or in a helper it shares with merge and split, which load whole rows) that is
    /// not in this list is always blank.
    /// </summary>
    internal static readonly SongColumnSet DetectColumns = new(
        nameof(SongMetadata.Artist),
        nameof(SongMetadata.AlbumArtist),
        nameof(SongMetadata.Artists),
        nameof(SongMetadata.ArtistMusicBrainzIds),
        nameof(SongMetadata.AlbumArtistMusicBrainzId));

    public async Task<ArtistDuplicateReport> DetectAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var songs = await QueryEligible(ownerUserId).WithOnly(db, DetectColumns).ToListAsync(ct);
        var dismissed = await LoadDismissedPairsAsync(ownerUserId, ct);

        return Detect(
            songs,
            dismissed,
            options.Value.ArtistMergeFuzzyThreshold,
            options.Value.ArtistMergeIdentityFuzzyThreshold,
            options.Value.DuplicateMaxBlockSize);
    }

    /// <summary>
    /// What <see cref="CountAsync"/> needs on a row it did not load itself: the columns detection
    /// reads, and the one the rest of <see cref="QueryEligible"/>'s filter tests.
    /// </summary>
    internal static readonly SongColumnSet CountColumns = DetectColumns.With(nameof(SongMetadata.OwnerUserId));

    public async Task<int> CountAsync(
        Guid ownerUserId, IReadOnlyList<SongMetadata> library, CancellationToken ct = default)
    {
        // The part of QueryEligible the shared read has not applied.
        var songs = library.Where(s => s.OwnerUserId == ownerUserId).ToList();
        var dismissed = await LoadDismissedPairsAsync(ownerUserId, ct);
        var threshold = options.Value.ArtistMergeFuzzyThreshold;
        var identityFloor = options.Value.ArtistMergeIdentityFuzzyThreshold;
        var maxIdBlock = options.Value.DuplicateMaxBlockSize;

        // The stamp and the detection take the same five arguments. Detect gets copies that carry
        // DetectColumns only — these rows hold other detectors' columns too, and a column the stamp
        // does not cover must be one Detect cannot see.
        return memo.GetOrCount(
            nameof(ArtistDuplicateService),
            ownerUserId,
            StampOf(songs, dismissed, threshold, identityFloor, maxIdBlock),
            () =>
            {
                var report = Detect(DetectColumns.Copy(songs), dismissed, threshold, identityFloor, maxIdBlock);
                return report.Clusters.Count + report.CombinedCredits.Count;
            });
    }

    /// <summary>
    /// A fingerprint of everything <see cref="Detect"/> is given, parameter for parameter: its
    /// columns of its songs in order, the dismissed pairs (which it only looks up, so in a fixed
    /// order) and the three settings. A parameter added to one must be added to the other.
    /// </summary>
    internal static byte[] StampOf(
        IReadOnlyList<SongMetadata> songs,
        HashSet<(string Low, string High)> dismissed,
        double threshold,
        double identityFloor,
        int maxIdBlock)
    {
        using var stamp = new ContentStamp();
        DetectColumns.AddTo(stamp, songs);
        stamp.Add(dismissed.Count);
        foreach (var (low, high) in dismissed
            .OrderBy(p => p.Low, StringComparer.Ordinal)
            .ThenBy(p => p.High, StringComparer.Ordinal))
        {
            stamp.Add(low);
            stamp.Add(high);
        }
        stamp.Add(threshold);
        stamp.Add(identityFloor);
        stamp.Add(maxIdBlock);
        return stamp.Finish();
    }

    /// <summary>
    /// The detection itself, over one owner's songs already loaded. Static so that the rows, the
    /// dismissed pairs and the three settings are all it can see.
    /// </summary>
    internal static ArtistDuplicateReport Detect(
        IReadOnlyList<SongMetadata> songs,
        HashSet<(string Low, string High)> dismissed,
        double threshold,
        double identityFloor,
        int maxIdBlock)
    {
        var stats = CollectNameStats(songs);
        var credits = ArtistCreditEvidence.Build(songs, stats.Keys);

        // Only one artist's names participate in spelling clustering. A collab credit keys like —
        // or, off a MusicBrainz credit, carries the id of — its lead artist: "Kanye West feat. Kid
        // Cudi" and "Nas (featuring AZ)" search-key as the lead, "Hef met Jayh" holds Hef's id.
        // Clustering one as a "spelling variant" suggests a merge that silently DELETES the other
        // artists. Those surface in the combined-credit report instead (ArtistCreditEvidence says which
        // names are collabs). Placeholders ("Various Artists", "Unknown Artist") are excluded
        // outright: they are not an artist, they legitimately carry many different artists'
        // MusicBrainz ids, and merging one away would rename every compilation in the library to
        // whoever won the canonical slot.
        var names = credits.SingleArtistNames
            .Where(n => !credits.IsCollabCredit(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var keyOf = names.ToDictionary(n => n, TitleNormalizer.NormalizeForSearch, StringComparer.Ordinal);

        bool IsDismissed(string a, string b)
        {
            var (ka, kb) = (keyOf[a], keyOf[b]);
            var pair = string.CompareOrdinal(ka, kb) <= 0 ? (ka, kb) : (kb, ka);
            return dismissed.Contains(pair);
        }

        // The single edge test. Evidence is always PAIRWISE: a shared MusicBrainz id corroborates a
        // spelling that already looks alike, it never stands on its own. Names routinely carry ids
        // that are not theirs — an elected AlbumArtist keeps the previous row's
        // AlbumArtistMusicBrainzId, a discrete Artists list falls out of step with its id list — so
        // an unguarded id union let one stray id fold unrelated artists together.
        bool HasPairEvidence(string a, string b)
        {
            if (IsDismissed(a, b))
                return false;
            var (ka, kb) = (keyOf[a], keyOf[b]);
            if (ka.Length > 0 && string.Equals(ka, kb, StringComparison.Ordinal))
                return true;

            var ratio = FuzzyTextMatch.RatioPreNormalized(ka, kb, a, b) ?? 0;
            if (ratio >= threshold)
                return true;
            return ratio >= identityFloor && stats[a].Mbids.Overlaps(stats[b].Mbids);
        }

        List<string> PairEvidence(string a, string b)
        {
            if (IsDismissed(a, b))
                return [];
            var (ka, kb) = (keyOf[a], keyOf[b]);
            if (ka.Length > 0 && string.Equals(ka, kb, StringComparison.Ordinal))
                return ["same name after normalization"];

            var why = new List<string>(2);
            var ratio = FuzzyTextMatch.RatioPreNormalized(ka, kb, a, b) ?? 0;
            if (ratio >= threshold)
                why.Add("similar spelling");
            if (ratio >= identityFloor && stats[a].Mbids.Overlaps(stats[b].Mbids))
                why.Add("same MusicBrainz artist id");
            return why;
        }

        // Candidate pairs only — cheap ways two names might be related, each still gated by
        // PairEvidence: (a) identical normalized key ("JAY-Z" / "JAYZ" / "Jaÿ-z" fold to "jayz"),
        // (b) a shared MusicBrainz artist id, (c) the same fuzzy bucket, keyed on the first
        // normalized character (ignoring a leading "the " so "The Notorious B.I.G." buckets with
        // "Notorious BIG") to keep the pairwise comparison tractable; short keys are excluded
        // (fuzzy on short names is noise).
        static char BucketOf(string key) =>
            key.StartsWith("the ", StringComparison.Ordinal) && key.Length > 4 ? key[4] : key[0];
        var byMbid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var mbid in stats[name].Mbids)
                (byMbid.TryGetValue(mbid, out var bag) ? bag : byMbid[mbid] = []).Add(name);
        }
        // One id shared by more names than this is noise, not identity (a placeholder that slipped
        // the filter, a provider stamping the release artist onto every credit) — expanding it
        // pairwise only manufactures candidates.
        var candidateBlocks = names.GroupBy(n => keyOf[n]).Where(g => g.Key.Length > 0).Select(g => g.ToList())
            .Concat(byMbid.Values.Where(g => g.Count > 1 && g.Count <= maxIdBlock))
            .Concat(names.Where(n => keyOf[n].Length >= 4).GroupBy(n => BucketOf(keyOf[n])).Select(g => g.ToList()));

        // Union-find groups the corroborated edges into components. A component is only a shortlist:
        // union-find is transitive, and transitivity is what turned "a~b, b~c" into one cluster that
        // renamed unrelated artists. Star-splitting below re-checks every member against the
        // canonical it would actually be rewritten to.
        var parent = names.ToDictionary(n => n, n => n, StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        foreach (var block in candidateBlocks)
        {
            for (var i = 0; i < block.Count; i++)
            {
                for (var j = i + 1; j < block.Count; j++)
                {
                    if (Find(block[i]) == Find(block[j]) || !HasPairEvidence(block[i], block[j]))
                        continue;
                    var (ra, rb) = (Find(block[i]), Find(block[j]));
                    var (root, child) = string.CompareOrdinal(ra, rb) <= 0 ? (ra, rb) : (rb, ra);
                    parent[child] = root;
                }
            }
        }

        ArtistNameStat StatOf(string name) =>
            new(name, stats[name].Count, stats[name].Mbids.Order(StringComparer.Ordinal).ToList());
        static IOrderedEnumerable<ArtistNameStat> ByRank(IEnumerable<ArtistNameStat> variants) => variants
            .OrderByDescending(v => v.MusicBrainzIds.Count > 0)
            .ThenByDescending(v => v.SongCount)
            .ThenBy(v => v.Name, StringComparer.Ordinal);

        // A merge rewrites every variant to ONE canonical spelling, so every variant must be a
        // plausible variant of that canonical — not merely of some neighbour two hops away. Peel the
        // component into star-shaped clusters around its best-ranked member; whatever fails that
        // direct check re-forms its own star (or drops out as a lone name).
        var clusters = new List<ArtistDuplicateCluster>();
        foreach (var component in names.GroupBy(Find, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var remaining = ByRank(component.Select(StatOf)).ToList();
            while (remaining.Count > 1)
            {
                var canonical = remaining[0];
                var members = new List<ArtistNameStat> { canonical };
                var why = new HashSet<string>(StringComparer.Ordinal);
                var leftovers = new List<ArtistNameStat>();
                foreach (var other in remaining.Skip(1))
                {
                    var evidence = PairEvidence(canonical.Name, other.Name);
                    if (evidence.Count == 0)
                    {
                        leftovers.Add(other);
                        continue;
                    }
                    members.Add(other);
                    why.UnionWith(evidence);
                }
                if (members.Count > 1)
                    clusters.Add(new ArtistDuplicateCluster(
                        canonical.Name, members, why.Order(StringComparer.Ordinal).ToList()));
                remaining = leftovers;
            }
        }
        clusters = clusters
            .OrderByDescending(c => c.Variants.Sum(v => v.SongCount))
            .ThenBy(c => c.SuggestedCanonical, StringComparer.Ordinal)
            .ToList();

        // Combined credits: the credit of songs without a discrete Artists list, or the album artist
        // of any song, that the library shows to be several artists. Grouped by artist key — the key
        // SplitCreditAsync matches rows on — so two spellings of one credit are one card, and the
        // count is the very plan the Split button runs: offered and performed can't disagree.
        var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var rowsByKey = new Dictionary<string, List<SongMetadata>>(StringComparer.Ordinal);
        var creditCache = new Dictionary<string, (string Name, string Key)?>(StringComparer.Ordinal);
        void NoteCredit(string? raw, SongMetadata song)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;
            if (!creditCache.TryGetValue(raw, out var cached))
            {
                if (ArtistCreditNormalizer.NormalizeDisplayCredit(raw) is { } normName
                    && !ArtistCreditEvidence.IsPlaceholderName(normName))
                {
                    var normKey = TitleNormalizer.NormalizeArtistKey(normName);
                    cached = normKey.Length > 0 ? (normName, normKey) : null;
                }
                else
                {
                    cached = null;
                }
                creditCache[raw] = cached;
            }
            if (cached is not var (name, key))
                return;
            var bySpelling = spellings.TryGetValue(key, out var s) ? s : spellings[key] = new(StringComparer.Ordinal);
            bySpelling[name] = bySpelling.GetValueOrDefault(name) + 1;
            var rows = rowsByKey.TryGetValue(key, out var r) ? r : rowsByKey[key] = [];
            if (rows.Count == 0 || !ReferenceEquals(rows[^1], song))
                rows.Add(song);
        }
        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.Artists) || MultiValue.Split(song.Artists).Length == 0)
                NoteCredit(song.Artist, song);
            NoteCredit(song.AlbumArtist, song);
        }

        var combined = new List<CombinedCreditCandidate>();
        foreach (var (key, bySpelling) in spellings)
        {
            var credit = bySpelling
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;
            if (credits.OfferedParts(credit) is not { } parts)
                continue;
            var plan = credits.PlanSplit(credit, rowsByKey[key]);
            if (plan.Count > 0)
                combined.Add(new CombinedCreditCandidate(
                    credit, parts, plan.Count, plan.Count(e => e.MovesAlbumArtist)));
        }

        return new ArtistDuplicateReport(
            clusters,
            combined.OrderByDescending(c => c.SongCount).ThenBy(c => c.Credit, StringComparer.Ordinal).ToList());
    }

    public async Task<ArtistMergeResult> MergeAsync(
        Guid ownerUserId, string canonicalName, IReadOnlyList<string> variantNames, CancellationToken ct = default)
    {
        var canonical = ArtistCreditNormalizer.NormalizeDisplayCredit(canonicalName)
            ?? throw new ArgumentException("Canonical name is required.", nameof(canonicalName));
        if (ArtistCreditEvidence.IsPlaceholderName(canonical))
            throw new ArgumentException($"'{canonical}' is a placeholder, not an artist.", nameof(canonicalName));

        var songs = await QueryEligible(ownerUserId).ToListAsync(ct);
        var credits = ArtistCreditEvidence.Build(songs, CollectNameStats(songs).Keys);

        // Merging onto a collab credit would rewrite the lead's own rows to it ("Hef" → "Hef met
        // Jayh"): bug 3 run backwards. A combined credit is split, never merged onto.
        if (credits.IsCombinedCredit(canonical))
            throw new ArgumentException(
                $"'{canonical}' credits several artists, not one — split it instead.", nameof(canonicalName));
        var canonicalKey = TitleNormalizer.NormalizeArtistKey(canonical);

        // Variant keys plus the canonical's own key: casing/diacritic re-spellings of the canonical
        // itself also converge, and the alias row for the canonical key pins the display spelling.
        // Keys are ARTIST keys, which keep a featuring clause or bracketed text: under the search key
        // "Nas (featuring AZ)" keyed as "nas", so merging "NAS" → "Nas" rewrote that unselected row
        // and deleted AZ. Collab variants never join — a merge is for re-spellings of one name, and
        // "Hef met Jayh" → "Hef" deletes Jayh (and its alias would strip him on every re-enrich) —
        // nor does a variant the canonical is one part of, or that is a part of the canonical, when
        // the library holds no evidence yet. Placeholders never join either: aliasing "various
        // artists" onto a real name would have the album identity heal rename every compilation.
        var matchKeys = variantNames
            .Select(ArtistCreditNormalizer.NormalizeDisplayCredit)
            .OfType<string>()
            .Where(v => !ArtistCreditEvidence.IsPlaceholderName(v) && !credits.IsCollabCredit(v))
            .Where(v => !ArtistAliasMap.JoinsAsPart(v, canonicalKey)
                && !ArtistAliasMap.JoinsAsPart(canonical, TitleNormalizer.NormalizeArtistKey(v)))
            .Select(TitleNormalizer.NormalizeArtistKey)
            .Append(canonicalKey)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (matchKeys.Count == 0)
            throw new ArgumentException("At least one resolvable variant name is required.", nameof(variantNames));

        // The id the canonical spelling already carries, so rewritten rows stop carrying the
        // variants' ids. Leaving them behind is what made the last merge self-perpetuating: the
        // canonical name ended up holding every variant's MusicBrainz id, and the next detect run
        // proposed the very same merge again.
        var canonicalMbid = ResolveCanonicalMbid(songs, canonicalKey);

        var now = DateTime.UtcNow;
        var updated = 0;
        var requeued = 0;

        foreach (var song in songs)
        {
            var changes = new List<(string Field, string? Old, string? New, Action Apply)>();
            var discrete = ArtistCreditEvidence.DiscreteArtists(song);

            if (MatchesWholeField(song.Artist, matchKeys, canonical, discrete))
                changes.Add((nameof(SongMetadata.Artist), song.Artist, canonical, () => song.Artist = canonical));
            if (MatchesWholeField(song.AlbumArtist, matchKeys, canonical, discrete))
            {
                changes.Add((nameof(SongMetadata.AlbumArtist), song.AlbumArtist, canonical, () => song.AlbumArtist = canonical));
                if (canonicalMbid is not null && !string.Equals(song.AlbumArtistMusicBrainzId, canonicalMbid, StringComparison.Ordinal))
                    changes.Add((nameof(SongMetadata.AlbumArtistMusicBrainzId), song.AlbumArtistMusicBrainzId, canonicalMbid,
                        () => song.AlbumArtistMusicBrainzId = canonicalMbid));
            }

            var (newArtists, newArtistIds) = MapArtistsList(song, matchKeys, canonical, canonicalMbid);
            if (newArtists is not null)
            {
                var capturedArtists = newArtists;
                changes.Add((nameof(SongMetadata.Artists), song.Artists, capturedArtists, () => song.Artists = capturedArtists));
                if (newArtistIds.Changed)
                {
                    var capturedIds = newArtistIds.Value;
                    changes.Add((nameof(SongMetadata.ArtistMusicBrainzIds), song.ArtistMusicBrainzIds, capturedIds,
                        () => song.ArtistMusicBrainzIds = capturedIds));
                }
            }

            if (changes.Count == 0)
                continue;

            song.CaptureOriginalMetadata();
            foreach (var (field, oldValue, newValue, apply) in changes)
            {
                SongChangeAudit.Record(db, SongChangeAudit.ArtistMerge, now, song.Id, field, oldValue, newValue);
                apply();
            }

            updated++;
            // Re-tag the already-built file so the on-disk artist frames converge too (see
            // ArtistCreditHealer for the RequeueForRetag semantics).
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        // Persist the aliases even when no song matched right now — the point is durability against
        // future heals/enrichment reintroducing the variant. Rows written before the artist key
        // existed are keyed by the search form, the SAME string for every plain name, so this
        // lookup by key finds such a legacy row and updates it in place rather than inserting a
        // second one under the unique (OwnerUserId, AliasKey) index.
        var existingAliases = await db.ArtistAliases
            .IgnoreQueryFilters()
            .Where(a => a.OwnerUserId == ownerUserId && matchKeys.Contains(a.AliasKey))
            .ToDictionaryAsync(a => a.AliasKey, ct);
        var aliasesStored = 0;
        foreach (var key in matchKeys)
        {
            if (existingAliases.TryGetValue(key, out var alias))
            {
                if (!string.Equals(alias.CanonicalName, canonical, StringComparison.Ordinal))
                {
                    alias.CanonicalName = canonical;
                    aliasesStored++;
                }
            }
            else
            {
                db.ArtistAliases.Add(new ArtistAlias
                {
                    OwnerUserId = ownerUserId,
                    AliasKey = key,
                    CanonicalName = canonical,
                    CreatedAtUtc = now,
                });
                aliasesStored++;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Artist merge to '{Canonical}' for owner {OwnerUserId}: {Updated} songs updated, {Requeued} re-queued, {Aliases} aliases stored",
            canonical, ownerUserId, updated, requeued, aliasesStored);

        return new ArtistMergeResult(updated, requeued, aliasesStored);
    }

    public async Task<ArtistSplitCreditResult> SplitCreditAsync(
        Guid ownerUserId, string creditName, CancellationToken ct = default)
    {
        var credit = ArtistCreditNormalizer.NormalizeDisplayCredit(creditName)
            ?? throw new ArgumentException("Credit name is required.", nameof(creditName));

        // Every row, not only blank-Artists ones: the album-artist fix applies to rows that already
        // have a discrete list, and the rest of the library is the evidence that "+"/"and"/"met"
        // join two artists here rather than belong to one name.
        var songs = await QueryEligible(ownerUserId).ToListAsync(ct);
        var credits = ArtistCreditEvidence.Build(songs, CollectNameStats(songs).Keys);
        if (!credits.CanSplit(credit))
        {
            throw new ArgumentException(
                ArtistCreditNormalizer.SplitOnAmbiguousJoiners(credit).Count > 1
                    ? $"'{credit}' may be one artist's name: split it once each part exists as an artist on its own."
                    : $"'{credit}' does not split into multiple artists.",
                nameof(creditName));
        }

        var plan = credits.PlanSplit(credit, songs);

        // One stamp for the whole call: (Source, CreatedAtUtc) IS the batch the dedup history lists
        // and reverts, and SongFieldReverter both applies and restores each field, so a revert puts
        // back exactly what this wrote — the album artist and its id included.
        var now = DateTime.UtcNow;
        var requeued = 0;
        foreach (var edit in plan)
        {
            var song = edit.Song;
            song.CaptureOriginalMetadata();
            foreach (var (field, oldValue, newValue) in edit.Changes)
            {
                SongChangeAudit.Record(db, SongChangeAudit.ArtistCreditSplit, now, song.Id, field, oldValue, newValue);
                SongFieldReverter.Apply(song, field, newValue);
            }

            // Re-tag the built file, and — when the album artist moved to the lead — relocate it: the
            // builder resolves the destination from AlbumArtist, so a changed folder sets
            // PreviousDestinationPath and the old copy under "Hef met Jayh/" is pruned after the move.
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        if (plan.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Split combined credit '{Credit}' for owner {OwnerUserId}: {Updated} songs ({AlbumArtists} re-filed under the lead), {Requeued} re-queued",
                credit, ownerUserId, plan.Count, plan.Count(e => e.MovesAlbumArtist), requeued);
        }

        return new ArtistSplitCreditResult(plan.Count, requeued);
    }

    public async Task<int> DismissAsync(Guid ownerUserId, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var keys = names
            .Select(TitleNormalizer.NormalizeForSearch)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (keys.Count < 2)
            return 0;

        var existing = await LoadDismissedPairsAsync(ownerUserId, ct);
        var now = DateTime.UtcNow;
        var added = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            for (var j = i + 1; j < keys.Count; j++)
            {
                if (existing.Contains((keys[i], keys[j])))
                    continue;
                db.DedupDismissals.Add(new DedupDismissal
                {
                    OwnerUserId = ownerUserId,
                    Kind = DedupDismissalKind.ArtistPair,
                    KeyLow = keys[i],
                    KeyHigh = keys[j],
                    CreatedAtUtc = now,
                });
                added++;
            }
        }

        if (added > 0)
            await db.SaveChangesAsync(ct);
        return added;
    }

    private IQueryable<SongMetadata> QueryEligible(Guid ownerUserId) => db.Songs
        .IgnoreQueryFilters()
        .Where(s => s.OwnerUserId == ownerUserId)
        .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
        .ExcludingDemoTenant();

    private async Task<HashSet<(string Low, string High)>> LoadDismissedPairsAsync(
        Guid ownerUserId, CancellationToken ct)
    {
        var rows = await db.DedupDismissals
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.OwnerUserId == ownerUserId && d.Kind == DedupDismissalKind.ArtistPair)
            .Select(d => new { d.KeyLow, d.KeyHigh })
            .ToListAsync(ct);
        return rows.Select(r => (r.KeyLow, r.KeyHigh)).ToHashSet();
    }

    private sealed class NameStat
    {
        public int Count { get; private set; }
        public HashSet<string> Mbids { get; } = [];

        public void Bump(string? mbid)
        {
            if (!string.IsNullOrWhiteSpace(mbid))
                Mbids.Add(mbid.Trim());
            Count++;
        }
    }

    /// <summary>
    /// Per-name occurrence stats across the discrete Artists list, the AlbumArtist, and — only for
    /// songs without a discrete list — the raw display credit.
    /// </summary>
    private static Dictionary<string, NameStat> CollectNameStats(IReadOnlyList<SongMetadata> songs)
    {
        var stats = new Dictionary<string, NameStat>(StringComparer.Ordinal);
        var displayCache = new Dictionary<string, string?>(StringComparer.Ordinal);

        void Bump(Dictionary<string, NameStat> map, string? rawName, string? mbid)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return;
            if (!displayCache.TryGetValue(rawName, out var name))
            {
                name = ArtistCreditNormalizer.NormalizeDisplayCredit(rawName);
                displayCache[rawName] = name;
            }
            if (name is null)
                return;
            if (!map.TryGetValue(name, out var stat))
                map[name] = stat = new NameStat();
            stat.Bump(mbid);
        }

        foreach (var song in songs)
        {
            var discrete = MultiValue.Split(song.Artists);
            var mbids = MultiValue.Split(song.ArtistMusicBrainzIds);
            var aligned = mbids.Length == discrete.Length;

            if (discrete.Length > 0)
            {
                for (var i = 0; i < discrete.Length; i++)
                    Bump(stats, discrete[i], aligned ? mbids[i] : null);
            }
            else
            {
                Bump(stats, song.Artist, null);
            }

            Bump(stats, song.AlbumArtist, song.AlbumArtistMusicBrainzId);
        }

        return stats;
    }

    /// <summary>
    /// The MusicBrainz artist id the canonical spelling already carries most often, across both the
    /// album artist and the aligned discrete artists list. Null when the canonical has none — then a
    /// rewritten row keeps the id it had rather than losing identity it may be the only source of.
    /// Matched by artist key, so a collab album artist ("Nas (featuring AZ)", keyed "nas" by the
    /// search form) never lends its id to the canonical.
    /// </summary>
    private static string? ResolveCanonicalMbid(IReadOnlyList<SongMetadata> songs, string canonicalKey)
    {
        if (canonicalKey.Length == 0)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Count(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id))
                counts[id.Trim()] = counts.GetValueOrDefault(id.Trim()) + 1;
        }

        foreach (var song in songs)
        {
            if (TitleNormalizer.NormalizeArtistKey(song.AlbumArtist) == canonicalKey)
                Count(song.AlbumArtistMusicBrainzId);

            var names = MultiValue.Split(song.Artists);
            var ids = MultiValue.Split(song.ArtistMusicBrainzIds);
            if (ids.Length != names.Length)
                continue;
            for (var i = 0; i < names.Length; i++)
            {
                if (TitleNormalizer.NormalizeArtistKey(names[i]) == canonicalKey)
                    Count(ids[i]);
            }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    private static bool MatchesWholeField(
        string? value, HashSet<string> matchKeys, string canonical, IReadOnlyList<string> discrete)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, canonical, StringComparison.Ordinal))
            return false;
        // A multi-part credit is never rewritten whole — that deletes every other artist in it. The
        // artist key already keeps a feat clause apart from its lead's key; this also refuses the
        // forms it can't see: a split credit ("JAYZ & X"), or one this very row's discrete list
        // shows to be joined ("Hef met Jayh" over [Hef, Jayh]). The discrete list still maps its
        // own segments.
        if (ArtistCreditNormalizer.SplitArtists(value).Count > 1
            || ArtistCreditNormalizer.IsJoinedCreditOf(value, discrete))
            return false;
        var key = TitleNormalizer.NormalizeArtistKey(value);
        return key.Length > 0 && matchKeys.Contains(key);
    }

    /// <summary>
    /// Maps matching segments of the discrete Artists list to the canonical spelling, de-duplicating
    /// segments that collapse together and keeping the positionally-aligned MBID list in step (a
    /// rewritten segment takes the canonical's own id when there is one, else the first occurrence's
    /// id wins). Returns (null, _) when nothing changed.
    /// </summary>
    private static (string? Artists, (bool Changed, string? Value) ArtistIds) MapArtistsList(
        SongMetadata song, HashSet<string> matchKeys, string canonical, string? canonicalMbid)
    {
        var names = MultiValue.Split(song.Artists);
        if (names.Length == 0)
            return (null, (false, null));

        var ids = MultiValue.Split(song.ArtistMusicBrainzIds);
        var aligned = ids.Length == names.Length;

        var outNames = new List<string>(names.Length);
        var outIds = new List<string>(names.Length);
        var changed = false;
        for (var i = 0; i < names.Length; i++)
        {
            // A segment that is itself several artists (a legacy "A feat. B" segment, or the joined
            // form of the other segments) is left alone, exactly like a whole field.
            var isCredit = ArtistCreditNormalizer.SplitArtists(names[i]).Count > 1
                || (names.Length > 2
                    && ArtistCreditNormalizer.IsJoinedCreditOf(names[i], names.Where((_, j) => j != i).ToList()));
            var key = TitleNormalizer.NormalizeArtistKey(names[i]);
            var isCanonical = !isCredit && key.Length > 0 && matchKeys.Contains(key);
            var mapped = isCanonical ? canonical : names[i];
            changed |= !string.Equals(mapped, names[i], StringComparison.Ordinal);
            // A segment that becomes the canonical takes the canonical's id, never the variant's —
            // a foreign id left under the canonical spelling is what re-proposes the same merge.
            var mappedId = isCanonical && canonicalMbid is not null ? canonicalMbid : aligned ? ids[i] : string.Empty;

            var existingIdx = outNames.FindIndex(n => string.Equals(n, mapped, StringComparison.Ordinal));
            if (existingIdx >= 0)
            {
                changed = true; // segment collapsed onto an earlier one
                if (aligned && outIds[existingIdx].Length == 0 && mappedId.Length > 0)
                    outIds[existingIdx] = mappedId;
                continue;
            }

            outNames.Add(mapped);
            outIds.Add(aligned ? mappedId : string.Empty);
        }

        if (!changed)
            return (null, (false, null));

        var newArtists = MultiValue.Join(outNames);
        if (!aligned)
            return (newArtists, (false, null));

        var newIds = outIds.All(id => id.Length == 0) ? null : MultiValue.Join(outIds);
        var idsChanged = !string.Equals(newIds, song.ArtistMusicBrainzIds, StringComparison.Ordinal);
        return (newArtists, (idsChanged, newIds));
    }
}
