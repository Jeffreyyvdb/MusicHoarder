using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

public record DuplicateDetectionResult(
    int GroupsFound,
    int DuplicatesFlagged,
    int DuplicatesCleared,
    int SuspectedPairs,
    TimeSpan Duration);

public interface IDuplicateDetectionService
{
    Task<DuplicateDetectionResult> DetectDuplicatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Computes a quality score for a song. Codec tier dominates (lossless always beats lossy);
    /// bitrate breaks ties within a tier. Ties are broken by file size (larger = higher quality).
    /// </summary>
    static int QualityScore(SongMetadata song) => AudioQuality.Score(song);

    /// <summary>
    /// Keeper election for a duplicate cluster, shared by detection and the duplicates endpoint. A
    /// user-pinned keeper always wins; otherwise quality first, then metadata trustworthiness (a
    /// Matched copy carries verified tags, while an unmatched twin may be a mislabeled file), then
    /// keep-the-built-copy (flagging it would orphan its destination file and rebuild the same
    /// audio under a new name).
    /// </summary>
    static List<SongMetadata> RankKeeperFirst(IEnumerable<SongMetadata> cluster) => cluster
        .OrderByDescending(s => s.DuplicateKeeperPinnedAtUtc ?? DateTime.MinValue)
        .ThenByDescending(QualityScore)
        .ThenByDescending(s => s.EnrichmentStatus == EnrichmentStatus.Matched)
        .ThenByDescending(s => s.LibraryBuildStatus == LibraryBuildStatus.Done && s.DestinationPath != null)
        .ThenByDescending(s => s.FileSizeBytes)
        .ThenBy(s => s.Id)
        .ToList();
}

/// <summary>
/// Finds duplicate songs per owner and persists the evidence as pairwise
/// <see cref="SongDuplicateLink"/> rows. Candidates come from exact fingerprint equality, shared
/// AcoustID/ISRC identifiers, and normalized artist+title blocking; non-exact candidates are
/// confirmed (or rejected) by decoded Chromaprint similarity. Only <em>Confirmed</em> clusters are
/// projected onto <see cref="SongMetadata.IsDuplicate"/> — Suspected pairs surface in the UI only,
/// so a fuzzy metadata guess can never silently drop a file from the build.
/// </summary>
public class DuplicateDetectionService(
    IServiceScopeFactory scopeFactory,
    IFingerprintSimilarityGate fingerprintGate,
    IOptionsMonitor<MusicEnricherOptions> options,
    ILogger<DuplicateDetectionService> logger) : IDuplicateDetectionService
{
    public async Task<DuplicateDetectionResult> DetectDuplicatesAsync(CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var opts = options.CurrentValue;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var songs = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .ExcludingDemoTenant()
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        var allLinks = await db.SongDuplicateLinks
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        var linksByOwner = allLinks
            .GroupBy(l => l.OwnerUserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Owners whose every song vanished still have link rows; their Active links are all stale.
        var ownersWithSongs = songs.Select(s => s.OwnerUserId).ToHashSet();
        foreach (var (owner, ownerLinks) in linksByOwner)
        {
            if (!ownersWithSongs.Contains(owner))
                db.SongDuplicateLinks.RemoveRange(ownerLinks.Where(l => l.Status == DuplicateLinkStatus.Active));
        }

        var groupsFound = 0;
        var duplicatesFlagged = 0;
        var duplicatesCleared = 0;
        var suspectedPairs = 0;

        foreach (var ownerGroup in songs.GroupBy(s => s.OwnerUserId))
        {
            ct.ThrowIfCancellationRequested();

            var outcome = ProcessOwner(
                db,
                ownerGroup.Key,
                ownerGroup.ToList(),
                linksByOwner.GetValueOrDefault(ownerGroup.Key) ?? [],
                opts);

            groupsFound += outcome.Groups;
            duplicatesFlagged += outcome.Flagged;
            duplicatesCleared += outcome.Cleared;
            suspectedPairs += outcome.Suspected;
        }

        await db.SaveChangesAsync(ct);

        var duration = DateTime.UtcNow - startedAt;

        logger.LogInformation(
            "Duplicate detection complete: {Groups} confirmed groups, {Flagged} flagged, {Cleared} cleared, {Suspected} suspected pairs, Duration={Duration:F1}s",
            groupsFound, duplicatesFlagged, duplicatesCleared, suspectedPairs, duration.TotalSeconds);

        return new DuplicateDetectionResult(groupsFound, duplicatesFlagged, duplicatesCleared, suspectedPairs, duration);
    }

    private sealed record OwnerOutcome(int Groups, int Flagged, int Cleared, int Suspected);

    private sealed record PairVerdict(DuplicateMatchReason Reasons, DuplicateConfidence Confidence, double? Similarity);

    private OwnerOutcome ProcessOwner(
        MusicHoarderDbContext db,
        Guid ownerId,
        List<SongMetadata> ownerSongs,
        List<SongDuplicateLink> existingLinks,
        MusicEnricherOptions opts)
    {
        var byId = ownerSongs.ToDictionary(s => s.Id);
        var candidates = GenerateCandidates(ownerId, ownerSongs, opts);
        var verdicts = ConfirmCandidates(candidates, byId, opts);

        // --- Upsert links (never touch Dismissed rows; drop Active rows no longer detected) ---
        var existingByPair = existingLinks.ToDictionary(l => (l.SongIdLow, l.SongIdHigh));

        foreach (var (pair, verdict) in verdicts)
        {
            if (existingByPair.TryGetValue(pair, out var link))
            {
                if (link.Status == DuplicateLinkStatus.Dismissed)
                    continue;
                link.Reasons = verdict.Reasons;
                link.Confidence = verdict.Confidence;
                link.Similarity = verdict.Similarity;
            }
            else
            {
                db.SongDuplicateLinks.Add(new SongDuplicateLink
                {
                    OwnerUserId = ownerId,
                    SongIdLow = pair.Low,
                    SongIdHigh = pair.High,
                    Reasons = verdict.Reasons,
                    Confidence = verdict.Confidence,
                    Similarity = verdict.Similarity,
                    Status = DuplicateLinkStatus.Active,
                    DetectedAtUtc = DateTime.UtcNow,
                });
            }
        }

        foreach (var link in existingLinks)
        {
            if (link.Status == DuplicateLinkStatus.Active && !verdicts.ContainsKey((link.SongIdLow, link.SongIdHigh)))
                db.SongDuplicateLinks.Remove(link);
        }

        // --- Project Confirmed clusters onto IsDuplicate via union-find + keeper election ---
        var unionFind = new UnionFind();
        foreach (var (pair, verdict) in verdicts)
        {
            if (verdict.Confidence != DuplicateConfidence.Confirmed)
                continue;
            if (existingByPair.TryGetValue(pair, out var link) && link.Status == DuplicateLinkStatus.Dismissed)
                continue;
            unionFind.Union(pair.Low, pair.High);
        }

        var clusters = unionFind.Clusters()
            .Select(ids => ids.Select(id => byId[id]).ToList())
            .Where(members => members.Count > 1)
            .ToList();

        var flagged = 0;
        var cleared = 0;
        var currentDuplicateIds = new HashSet<int>();

        foreach (var cluster in clusters)
        {
            var ranked = IDuplicateDetectionService.RankKeeperFirst(cluster);
            var best = ranked[0];

            if (best.IsDuplicate)
            {
                best.ClearDuplicate();
                cleared++;
            }

            for (var i = 1; i < ranked.Count; i++)
            {
                var dup = ranked[i];
                currentDuplicateIds.Add(dup.Id);

                if (!dup.IsDuplicate || dup.DuplicateOfId != best.Id)
                {
                    dup.MarkAsDuplicate(best.Id);
                    flagged++;
                }
            }
        }

        foreach (var song in ownerSongs.Where(s => s.IsDuplicate && !currentDuplicateIds.Contains(s.Id)))
        {
            song.ClearDuplicate();
            cleared++;
        }

        var suspected = verdicts.Count(v => v.Value.Confidence == DuplicateConfidence.Suspected);
        return new OwnerOutcome(clusters.Count, flagged, cleared, suspected);
    }

    private Dictionary<(int Low, int High), DuplicateMatchReason> GenerateCandidates(
        Guid ownerId,
        List<SongMetadata> ownerSongs,
        MusicEnricherOptions opts)
    {
        var candidates = new Dictionary<(int Low, int High), DuplicateMatchReason>();

        void AddPair(SongMetadata a, SongMetadata b, DuplicateMatchReason reason)
        {
            var key = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
            candidates[key] = candidates.GetValueOrDefault(key) | reason;
        }

        // Live/remix/acoustic/etc. never pairs with the studio recording, no matter how well the
        // normalized text or identifiers agree (compilations reuse ISRCs across masters).
        static bool QualifiersCompatible(SongMetadata a, SongMetadata b) =>
            VersionQualifier.Compare(VersionQualifier.Detect(a.Title), VersionQualifier.Detect(b.Title));

        static bool DurationsWithin(SongMetadata a, SongMetadata b, int toleranceSeconds, bool requireBoth)
        {
            if (a.DurationSeconds is not int da || b.DurationSeconds is not int db)
                return !requireBoth;
            return Math.Abs(da - db) <= toleranceSeconds;
        }

        void AddGroupPairs(
            IEnumerable<IGrouping<string, SongMetadata>> groups,
            DuplicateMatchReason reason,
            string blockKind,
            Func<SongMetadata, SongMetadata, bool> pairGuard)
        {
            foreach (var group in groups)
            {
                var members = group.ToList();
                if (members.Count < 2)
                    continue;

                if (members.Count > opts.DuplicateMaxBlockSize)
                {
                    logger.LogWarning(
                        "Skipping pathological duplicate-candidate block ({Kind}, {Count} songs, owner {OwnerUserId}): key {Key}",
                        blockKind, members.Count, ownerId, group.Key);
                    continue;
                }

                for (var i = 0; i < members.Count; i++)
                    for (var j = i + 1; j < members.Count; j++)
                        if (pairGuard(members[i], members[j]))
                            AddPair(members[i], members[j], reason);
            }
        }

        // Exact fingerprint equality — byte-identical audio; no further guards needed.
        AddGroupPairs(
            ownerSongs.Where(s => !string.IsNullOrEmpty(s.Fingerprint)).GroupBy(s => s.Fingerprint!),
            DuplicateMatchReason.ExactFingerprint,
            "fingerprint",
            (_, _) => true);

        // Shared AcoustID track id — strong identifier, but guard against tag drift with a loose
        // duration check (when both known) and the strong-qualifier gate.
        var identifierTolerance = opts.DuplicateDurationToleranceSeconds * 2;
        AddGroupPairs(
            ownerSongs.Where(s => !string.IsNullOrWhiteSpace(s.AcoustIdTrackId)).GroupBy(s => s.AcoustIdTrackId!),
            DuplicateMatchReason.AcoustIdTrack,
            "acoustid",
            (a, b) => QualifiersCompatible(a, b) && DurationsWithin(a, b, identifierTolerance, requireBoth: false));

        // Shared ISRC — candidate only (dirty tags share ISRCs); confirmation still requires audio.
        AddGroupPairs(
            ownerSongs
                .Select(s => (Song: s, Isrc: ProviderIdentity.NormalizeIsrc(s.Isrc)))
                .Where(x => x.Isrc.Length > 0)
                .GroupBy(x => x.Isrc, x => x.Song),
            DuplicateMatchReason.Isrc,
            "isrc",
            (a, b) => QualifiersCompatible(a, b) && DurationsWithin(a, b, identifierTolerance, requireBoth: false));

        // Metadata blocking: normalized primary artist + title, durations required and within
        // tolerance. This is what catches a FLAC and an MP3 of the same recording whose
        // fingerprints differ as strings.
        AddGroupPairs(
            ownerSongs
                .Select(s => (Song: s, Key: MetadataBlockKey(s)))
                .Where(x => x.Key is not null)
                .GroupBy(x => x.Key!, x => x.Song),
            DuplicateMatchReason.Metadata,
            "metadata",
            (a, b) => QualifiersCompatible(a, b)
                      && DurationsWithin(a, b, opts.DuplicateDurationToleranceSeconds, requireBoth: true));

        return candidates;
    }

    private static string? MetadataBlockKey(SongMetadata song)
    {
        var artist = ArtistCreditNormalizer.GetPrimaryArtist(song.Artist) ?? song.Artist;
        var artistKey = TitleNormalizer.NormalizeForSearch(artist);
        var titleKey = TitleNormalizer.NormalizeForSearch(song.Title);
        if (artistKey.Length == 0 || titleKey.Length == 0)
            return null;
        return $"{artistKey}\u0001{titleKey}";
    }

    private Dictionary<(int Low, int High), PairVerdict> ConfirmCandidates(
        Dictionary<(int Low, int High), DuplicateMatchReason> candidates,
        Dictionary<int, SongMetadata> byId,
        MusicEnricherOptions opts)
    {
        // Decode each fingerprint at most once per run — the pairwise-compare cost control.
        var decodeCache = new Dictionary<int, uint[]?>();
        uint[]? Frames(SongMetadata song)
        {
            if (!decodeCache.TryGetValue(song.Id, out var frames))
            {
                frames = fingerprintGate.TryDecode(song.Fingerprint, out var decoded) ? decoded : null;
                decodeCache[song.Id] = frames;
            }
            return frames;
        }

        var verdicts = new Dictionary<(int Low, int High), PairVerdict>();

        foreach (var (pair, reasons) in candidates)
        {
            if (reasons.HasFlag(DuplicateMatchReason.ExactFingerprint))
            {
                verdicts[pair] = new PairVerdict(reasons, DuplicateConfidence.Confirmed, 1.0);
                continue;
            }

            var framesA = Frames(byId[pair.Low]);
            var framesB = Frames(byId[pair.High]);

            if (framesA is not null && framesB is not null)
            {
                var similarity = fingerprintGate.Similarity(framesA, framesB);
                if (similarity >= opts.DuplicateFingerprintMinSimilarity)
                {
                    verdicts[pair] = new PairVerdict(
                        reasons | DuplicateMatchReason.FingerprintSimilarity, DuplicateConfidence.Confirmed, similarity);
                }
                else if (similarity < opts.DuplicateFingerprintRejectSimilarity)
                {
                    // Decodable fingerprints that strongly disagree are affirmative evidence of
                    // different recordings — don't surface the pair at all.
                }
                else
                {
                    verdicts[pair] = new PairVerdict(reasons, DuplicateConfidence.Suspected, similarity);
                }
            }
            else
            {
                // No audio evidence available: metadata/identifier agreement alone is never enough
                // to auto-flag, but it's worth a human look.
                verdicts[pair] = new PairVerdict(reasons, DuplicateConfidence.Suspected, null);
            }
        }

        return verdicts;
    }

    private sealed class UnionFind
    {
        private readonly Dictionary<int, int> _parent = [];

        private int Find(int x)
        {
            if (!_parent.TryGetValue(x, out var p))
            {
                _parent[x] = x;
                return x;
            }
            if (p == x)
                return x;
            var root = Find(p);
            _parent[x] = root;
            return root;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
                _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        public IEnumerable<List<int>> Clusters() =>
            _parent.Keys.ToList().GroupBy(Find).Select(g => g.ToList());
    }
}
