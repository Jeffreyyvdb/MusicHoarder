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
    ILogger<ArtistDuplicateService> logger) : IArtistDuplicateService
{
    private const string MergeChangeSource = "artist-merge";
    private const string SplitChangeSource = "artist-credit-split";

    public async Task<ArtistDuplicateReport> DetectAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var songs = await QueryEligible(ownerUserId).AsNoTracking().ToListAsync(ct);
        var dismissed = await LoadDismissedPairsAsync(ownerUserId, ct);

        var stats = CollectNameStats(songs);
        var credits = CreditEvidence.Build(songs, stats.Keys);

        // Only one artist's names participate in spelling clustering. A collab credit keys like —
        // or, off a MusicBrainz credit, carries the id of — its lead artist: "Kanye West feat. Kid
        // Cudi" and "Nas (featuring AZ)" search-key as the lead, "Hef met Jayh" holds Hef's id.
        // Clustering one as a "spelling variant" suggests a merge that silently DELETES the other
        // artists. Those surface in the combined-credit report instead (CreditEvidence says which
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

        var threshold = options.Value.ArtistMergeFuzzyThreshold;
        var identityFloor = options.Value.ArtistMergeIdentityFuzzyThreshold;

        // The single edge test. Evidence is always PAIRWISE: a shared MusicBrainz id corroborates a
        // spelling that already looks alike, it never stands on its own. Names routinely carry ids
        // that are not theirs — an elected AlbumArtist keeps the previous row's
        // AlbumArtistMusicBrainzId, a discrete Artists list falls out of step with its id list — so
        // an unguarded id union let one stray id fold unrelated artists together.
        List<string> PairEvidence(string a, string b)
        {
            if (IsDismissed(a, b))
                return [];
            var (ka, kb) = (keyOf[a], keyOf[b]);
            if (ka.Length > 0 && string.Equals(ka, kb, StringComparison.Ordinal))
                return ["same name after normalization"];

            var why = new List<string>(2);
            var ratio = FuzzyTextMatch.Ratio(a, b) ?? 0;
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
        var maxIdBlock = options.Value.DuplicateMaxBlockSize;
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
                    if (Find(block[i]) == Find(block[j]) || PairEvidence(block[i], block[j]).Count == 0)
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
        void NoteCredit(string? raw, SongMetadata song)
        {
            if (ArtistCreditNormalizer.NormalizeDisplayCredit(raw) is not { } name || IsPlaceholderName(name))
                return;
            var key = TitleNormalizer.NormalizeArtistKey(name);
            if (key.Length == 0)
                return;
            var bySpelling = spellings.TryGetValue(key, out var s) ? s : spellings[key] = new(StringComparer.Ordinal);
            bySpelling[name] = bySpelling.GetValueOrDefault(name) + 1;
            var rows = rowsByKey.TryGetValue(key, out var r) ? r : rowsByKey[key] = [];
            if (rows.Count == 0 || !ReferenceEquals(rows[^1], song))
                rows.Add(song);
        }
        foreach (var song in songs)
        {
            if (MultiValue.Split(song.Artists).Length == 0)
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
        if (IsPlaceholderName(canonical))
            throw new ArgumentException($"'{canonical}' is a placeholder, not an artist.", nameof(canonicalName));

        var songs = await QueryEligible(ownerUserId).ToListAsync(ct);
        var credits = CreditEvidence.Build(songs, CollectNameStats(songs).Keys);

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
            .Where(v => !IsPlaceholderName(v) && !credits.IsCollabCredit(v))
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
            var discrete = DiscreteArtists(song);

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
                db.SongMetadataChanges.Add(new SongMetadataChange
                {
                    SongId = song.Id,
                    FieldName = field,
                    OldValue = oldValue,
                    NewValue = newValue,
                    Source = MergeChangeSource,
                    Confidence = 1.0,
                    CreatedAtUtc = now,
                    AppliedAtUtc = now,
                });
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
        var credits = CreditEvidence.Build(songs, CollectNameStats(songs).Keys);
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
                db.SongMetadataChanges.Add(new SongMetadataChange
                {
                    SongId = song.Id,
                    FieldName = field,
                    OldValue = oldValue,
                    NewValue = newValue,
                    Source = SplitChangeSource,
                    Confidence = 1.0,
                    CreatedAtUtc = now,
                    AppliedAtUtc = now,
                });
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

    private sealed record NameStat(int Count, HashSet<string> Mbids)
    {
        public NameStat Bump(string? mbid)
        {
            if (!string.IsNullOrWhiteSpace(mbid))
                Mbids.Add(mbid.Trim());
            return this with { Count = Count + 1 };
        }
    }

    /// <summary>
    /// Per-name occurrence stats across the discrete Artists list, the AlbumArtist, and — only for
    /// songs without a discrete list — the raw display credit.
    /// </summary>
    private static Dictionary<string, NameStat> CollectNameStats(IReadOnlyList<SongMetadata> songs)
    {
        var stats = new Dictionary<string, NameStat>(StringComparer.Ordinal);

        void Bump(Dictionary<string, NameStat> map, string? rawName, string? mbid)
        {
            var name = ArtistCreditNormalizer.NormalizeDisplayCredit(rawName);
            if (name is null)
                return;
            map[name] = (map.TryGetValue(name, out var stat) ? stat : new NameStat(0, [])).Bump(mbid);
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
    private static bool IsPlaceholderName(string name)
    {
        var trimmed = name.Trim();
        return DestinationPathResolver.IsVariousArtistsSentinel(trimmed) || PlaceholderNames.Contains(trimmed);
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

    /// <summary>A row's discrete Artists list minus lone "feat."/"with" segments — the phantom artist
    /// the old capturing split wrote into ARTISTS, which must never count as an artist or as proof
    /// that a credit is joined.</summary>
    private static string[] DiscreteArtists(SongMetadata song) =>
        MultiValue.Split(song.Artists).Where(s => !ArtistCreditNormalizer.IsDelimiterToken(s)).ToArray();

    /// <summary>One song a split would change, and the (field, old, new) changes it would make.</summary>
    private sealed record SplitEdit(
        SongMetadata Song, IReadOnlyList<(string Field, string? Old, string? New)> Changes, bool MovesAlbumArtist);

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
    private sealed class CreditEvidence
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

        private CreditEvidence(
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

        public static CreditEvidence Build(IReadOnlyList<SongMetadata> songs, IEnumerable<string> names)
        {
            var single = names
                .Where(n => ArtistCreditNormalizer.SplitArtists(n).Count == 1)
                .Where(n => !IsPlaceholderName(n) && !ArtistCreditNormalizer.IsDelimiterToken(n))
                .ToList();

            var lists = new Dictionary<string, Dictionary<string, (int Count, string[] List)>>(StringComparer.Ordinal);
            var idOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in songs)
            {
                var raw = MultiValue.Split(song.Artists);
                var ids = MultiValue.Split(song.ArtistMusicBrainzIds);
                if (ids.Length == raw.Length)
                {
                    for (var i = 0; i < raw.Length; i++)
                    {
                        var owners = idOwners.TryGetValue(ids[i], out var o) ? o : idOwners[ids[i]] = new(StringComparer.Ordinal);
                        owners.Add(TitleNormalizer.NormalizeArtistKey(raw[i]));
                    }
                }

                var discrete = DiscreteArtists(song);
                if (discrete.Length < 2)
                    continue;
                var joined = MultiValue.Join(discrete)!;
                foreach (var key in new[] { song.Artist, song.AlbumArtist }
                    .Where(v => ArtistCreditNormalizer.IsJoinedCreditOf(v, discrete))
                    .Select(TitleNormalizer.NormalizeArtistKey)
                    .Distinct(StringComparer.Ordinal))
                {
                    var byList = lists.TryGetValue(key, out var l) ? l : lists[key] = new(StringComparer.Ordinal);
                    byList[joined] = (byList.GetValueOrDefault(joined).Count + 1, discrete);
                }
            }

            var joinedLists = lists.ToDictionary(
                kv => kv.Key,
                kv => kv.Value
                    .OrderByDescending(x => x.Value.Count)
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .First().Value.List,
                StringComparer.Ordinal);
            return new CreditEvidence(single, joinedLists, idOwners);
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
    }
}
