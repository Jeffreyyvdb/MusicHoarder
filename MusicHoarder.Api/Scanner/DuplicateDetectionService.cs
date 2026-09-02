using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
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
/// <see cref="SongDuplicateLink"/> rows. Candidates come from
/// <see cref="DuplicateCandidateGenerator"/> (exact fingerprint equality, shared AcoustID/ISRC
/// identifiers, normalized artist+title blocking) and are confirmed or rejected by
/// <see cref="DuplicatePairConfirmer"/> (decoded Chromaprint similarity); this service owns the
/// per-owner sweep, the link upserts, and the projection of <em>Confirmed</em> clusters onto
/// <see cref="SongMetadata.IsDuplicate"/>. Suspected pairs surface in the UI only, so a fuzzy
/// metadata guess can never silently drop a file from the build.
/// </summary>
public class DuplicateDetectionService(
    IServiceScopeFactory scopeFactory,
    DuplicateCandidateGenerator candidateGenerator,
    DuplicatePairConfirmer pairConfirmer,
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

    private OwnerOutcome ProcessOwner(
        MusicHoarderDbContext db,
        Guid ownerId,
        List<SongMetadata> ownerSongs,
        List<SongDuplicateLink> existingLinks,
        MusicEnricherOptions opts)
    {
        var byId = ownerSongs.ToDictionary(s => s.Id);
        var candidates = candidateGenerator.Generate(ownerId, ownerSongs, opts);
        var verdicts = pairConfirmer.Confirm(candidates, byId, opts);

        // --- Upsert links (never touch Dismissed rows; drop Active rows no longer detected) ---
        var existingByPair = existingLinks.ToDictionary(l => SongIdPair.Of(l));

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
            if (link.Status == DuplicateLinkStatus.Active && !verdicts.ContainsKey(SongIdPair.Of(link)))
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
