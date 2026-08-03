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

/// <summary>A display credit ("A &amp; B") whose parts each exist as standalone artists, on songs
/// that carry no discrete Artists list — the split-artist defect the artists grid inherits.</summary>
public sealed record CombinedCreditCandidate(string Credit, IReadOnlyList<string> Parts, int SongCount);

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
    /// and re-enrichment can't reintroduce the variants. V1 limitation: multi-artist display
    /// credits ("A &amp; B") are not partially rewritten.
    /// </summary>
    Task<ArtistMergeResult> MergeAsync(
        Guid ownerUserId, string canonicalName, IReadOnlyList<string> variantNames, CancellationToken ct = default);

    /// <summary>Backfills the discrete Artists list for songs whose display credit equals the given
    /// combined credit and whose Artists field is blank.</summary>
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

        var stats = CollectNameStats(songs, out var creditOnlyCounts);

        // Only single-artist names participate in spelling clustering. A multi-part display credit
        // ("Kanye West feat. Kid Cudi", "A & B") normalizes to its primary artist's key — clustering
        // it as a "spelling variant" would suggest a merge that silently DELETES the featuring
        // credit. Those names surface in the combined-credit report instead.
        var names = stats.Keys
            .Where(n => ArtistCreditNormalizer.SplitArtists(n).Count == 1)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var keyOf = names.ToDictionary(n => n, TitleNormalizer.NormalizeForSearch, StringComparer.Ordinal);

        bool IsDismissed(string a, string b)
        {
            var (ka, kb) = (keyOf[a], keyOf[b]);
            var pair = string.CompareOrdinal(ka, kb) <= 0 ? (ka, kb) : (kb, ka);
            return dismissed.Contains(pair);
        }

        // Union-find over display spellings; evidence per union edge feeds the cluster report.
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
        var evidence = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Union(string a, string b, string why)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra == rb)
                return;
            var (root, child) = string.CompareOrdinal(ra, rb) <= 0 ? (ra, rb) : (rb, ra);
            parent[child] = root;
            var bag = evidence.TryGetValue(root, out var e) ? e : evidence[root] = [];
            if (evidence.TryGetValue(child, out var childBag))
                bag.UnionWith(childBag);
            bag.Add(why);
        }

        // (a) identical normalized key — "JAY-Z" / "JAYZ" / "Jaÿ-z" all fold to "jayz".
        foreach (var group in names.GroupBy(n => keyOf[n]).Where(g => g.Key.Length > 0))
        {
            var members = group.ToList();
            for (var i = 1; i < members.Count; i++)
                Union(members[0], members[i], "same name after normalization");
        }

        // (b) shared MusicBrainz artist id across different spellings — same artist by identity.
        var byMbid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var mbid in stats[name].Mbids)
            {
                if (byMbid.TryGetValue(mbid, out var first))
                {
                    if (!IsDismissed(first, name))
                        Union(first, name, "same MusicBrainz artist id");
                }
                else
                {
                    byMbid[mbid] = name;
                }
            }
        }

        // (c) fuzzy spelling distance, bucketed by first normalized character (ignoring a leading
        // "the " so "The Notorious B.I.G." buckets with "Notorious BIG") to keep the pairwise
        // comparison tractable; short keys are excluded (fuzzy on short names is noise).
        var threshold = options.Value.ArtistMergeFuzzyThreshold;
        static char BucketOf(string key) =>
            key.StartsWith("the ", StringComparison.Ordinal) && key.Length > 4 ? key[4] : key[0];
        foreach (var bucket in names
                     .Where(n => keyOf[n].Length >= 4)
                     .GroupBy(n => BucketOf(keyOf[n])))
        {
            var members = bucket.ToList();
            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    if (keyOf[members[i]] == keyOf[members[j]])
                        continue; // already unioned by (a)
                    if (IsDismissed(members[i], members[j]))
                        continue;
                    if ((FuzzyTextMatch.Ratio(members[i], members[j]) ?? 0) >= threshold)
                        Union(members[i], members[j], "similar spelling");
                }
            }
        }

        var clusters = names
            .GroupBy(Find, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var variants = g
                    .Select(n => new ArtistNameStat(n, stats[n].Count, stats[n].Mbids.Order(StringComparer.Ordinal).ToList()))
                    .OrderByDescending(v => v.MusicBrainzIds.Count > 0)
                    .ThenByDescending(v => v.SongCount)
                    .ThenBy(v => v.Name, StringComparer.Ordinal)
                    .ToList();
                var why = evidence.TryGetValue(Find(g.Key), out var e)
                    ? e.Order(StringComparer.Ordinal).ToList()
                    : [];
                return new ArtistDuplicateCluster(variants[0].Name, variants, why);
            })
            .OrderByDescending(c => c.Variants.Sum(v => v.SongCount))
            .ThenBy(c => c.SuggestedCanonical, StringComparer.Ordinal)
            .ToList();

        // Combined credits: a display credit that splits into ≥2 parts, each existing as a
        // single-artist name somewhere in the library (regardless of which field it came from).
        var standaloneKeys = names
            .Select(n => keyOf[n])
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var combined = new List<CombinedCreditCandidate>();
        foreach (var (credit, count) in creditOnlyCounts)
        {
            var parts = ArtistCreditNormalizer.SplitArtists(credit);
            if (parts.Count < 2)
                continue;
            // An explicit featuring delimiter is unambiguous — the primary artist existing standalone
            // is corroboration enough. Ambiguous joiners ("&", ",", " x ") could be one legitimate
            // name (Earth, Wind & Fire), so every part must exist standalone before suggesting a split.
            var required = ArtistCreditNormalizer.HasFeaturingDelimiter(credit) ? parts.Take(1) : parts;
            if (required.All(p => standaloneKeys.Contains(TitleNormalizer.NormalizeForSearch(p))))
                combined.Add(new CombinedCreditCandidate(credit, parts, count));
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

        // Variant keys plus the canonical's own key: casing/diacritic re-spellings of the canonical
        // itself also converge, and the alias row for the canonical key pins the display spelling.
        var matchKeys = variantNames
            .Select(ArtistCreditNormalizer.NormalizeDisplayCredit)
            .Where(v => v is not null)
            .Select(v => TitleNormalizer.NormalizeForSearch(v))
            .Append(TitleNormalizer.NormalizeForSearch(canonical))
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (matchKeys.Count == 0)
            throw new ArgumentException("At least one resolvable variant name is required.", nameof(variantNames));

        var songs = await QueryEligible(ownerUserId).ToListAsync(ct);

        var now = DateTime.UtcNow;
        var updated = 0;
        var requeued = 0;

        foreach (var song in songs)
        {
            var changes = new List<(string Field, string? Old, string? New, Action Apply)>();

            if (MatchesWholeField(song.Artist, matchKeys, canonical))
                changes.Add((nameof(SongMetadata.Artist), song.Artist, canonical, () => song.Artist = canonical));
            if (MatchesWholeField(song.AlbumArtist, matchKeys, canonical))
                changes.Add((nameof(SongMetadata.AlbumArtist), song.AlbumArtist, canonical, () => song.AlbumArtist = canonical));

            var (newArtists, newArtistIds) = MapArtistsList(song, matchKeys, canonical);
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
        // future heals/enrichment reintroducing the variant.
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
        var parts = ArtistCreditNormalizer.SplitArtists(credit);
        if (parts.Count < 2)
            throw new ArgumentException($"'{credit}' does not split into multiple artists.", nameof(creditName));

        var creditKey = TitleNormalizer.NormalizeForSearch(credit);
        var discrete = MultiValue.Join(parts);

        var songs = await QueryEligible(ownerUserId)
            .Where(s => s.Artists == null || s.Artists == "")
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var updated = 0;
        var requeued = 0;

        foreach (var song in songs)
        {
            if (TitleNormalizer.NormalizeForSearch(song.Artist) != creditKey)
                continue;

            song.CaptureOriginalMetadata();
            db.SongMetadataChanges.Add(new SongMetadataChange
            {
                SongId = song.Id,
                FieldName = nameof(SongMetadata.Artists),
                OldValue = song.Artists,
                NewValue = discrete,
                Source = SplitChangeSource,
                Confidence = 1.0,
                CreatedAtUtc = now,
                AppliedAtUtc = now,
            });
            song.Artists = discrete;

            updated++;
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Split combined credit '{Credit}' into [{Parts}] for owner {OwnerUserId}: {Updated} songs, {Requeued} re-queued",
                credit, string.Join(", ", parts), ownerUserId, updated, requeued);
        }

        return new ArtistSplitCreditResult(updated, requeued);
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
    /// songs without a discrete list — the raw display credit. Credit-only names are tracked
    /// separately: they're the combined-credit candidates ("A &amp; B" as one artist).
    /// </summary>
    private static Dictionary<string, NameStat> CollectNameStats(
        IReadOnlyList<SongMetadata> songs, out Dictionary<string, int> creditOnlyCounts)
    {
        var stats = new Dictionary<string, NameStat>(StringComparer.Ordinal);
        creditOnlyCounts = new Dictionary<string, int>(StringComparer.Ordinal);

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
            else if (ArtistCreditNormalizer.NormalizeDisplayCredit(song.Artist) is { } credit)
            {
                Bump(stats, credit, null);
                creditOnlyCounts[credit] = creditOnlyCounts.GetValueOrDefault(credit) + 1;
            }

            Bump(stats, song.AlbumArtist, song.AlbumArtistMusicBrainzId);
        }

        return stats;
    }

    private static bool MatchesWholeField(string? value, HashSet<string> matchKeys, string canonical)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, canonical, StringComparison.Ordinal))
            return false;
        // A multi-part display credit ("JAYZ feat. X") normalizes to the variant's key because
        // normalization strips the feat-clause — a whole-field rewrite would delete the featuring
        // credit. Only single-artist values rewrite; the discrete Artists list still maps segments.
        if (ArtistCreditNormalizer.SplitArtists(value).Count > 1)
            return false;
        var key = TitleNormalizer.NormalizeForSearch(value);
        return key.Length > 0 && matchKeys.Contains(key);
    }

    /// <summary>
    /// Maps matching segments of the discrete Artists list to the canonical spelling, de-duplicating
    /// segments that collapse together and keeping the positionally-aligned MBID list in step (the
    /// first occurrence's id wins). Returns (null, _) when nothing changed.
    /// </summary>
    private static (string? Artists, (bool Changed, string? Value) ArtistIds) MapArtistsList(
        SongMetadata song, HashSet<string> matchKeys, string canonical)
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
            var key = TitleNormalizer.NormalizeForSearch(names[i]);
            var mapped = key.Length > 0 && matchKeys.Contains(key) ? canonical : names[i];
            changed |= !string.Equals(mapped, names[i], StringComparison.Ordinal);

            var existingIdx = outNames.FindIndex(n => string.Equals(n, mapped, StringComparison.Ordinal));
            if (existingIdx >= 0)
            {
                changed = true; // segment collapsed onto an earlier one
                if (aligned && outIds[existingIdx].Length == 0 && ids[i].Length > 0)
                    outIds[existingIdx] = ids[i];
                continue;
            }

            outNames.Add(mapped);
            outIds.Add(aligned ? ids[i] : string.Empty);
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
