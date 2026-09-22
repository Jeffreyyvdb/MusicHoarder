using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

/// <summary>
/// One copy inside a duplicate cluster, as <c>GET /api/library/duplicates</c> reports it. The song
/// columns are the ones the Inbox card renders; the rest is decided here so both clients agree on
/// who the keeper is, whether a copy is built, and what evidence linked it to the cluster.
/// </summary>
public sealed record DuplicateMemberDto(
    int Id,
    string SourcePath,
    string FileName,
    string Extension,
    long FileSizeBytes,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    string? Title,
    int? Year,
    int? TrackNumber,
    int? DurationSeconds,
    int? Bitrate,
    string? Fingerprint,
    bool IsDuplicate,
    int? DuplicateOfId,
    EnrichmentStatus EnrichmentStatus,
    string? DestinationPath,
    /// <summary>True when this copy is already built to the destination library.</summary>
    bool IsBuilt,
    /// <summary>True for the elected keeper — always <c>Members[0]</c> of its group.</summary>
    bool IsKeeper,
    /// <summary>True when the user pinned this copy as the keeper (pins outrank quality).</summary>
    bool IsPinned,
    /// <summary>"confirmed" when at least one of this copy's links is acoustically confirmed, else "suspected".</summary>
    string Confidence,
    /// <summary>Union of the match reasons over this copy's links, in flag order — see <see cref="DuplicateGroupProjection.DescribeReasons"/>.</summary>
    string[] Reasons,
    /// <summary>Highest Chromaprint similarity over this copy's links; null when none was measured.</summary>
    double? Similarity,
    /// <summary>Server-side keep priority: codec tier dominates, bitrate breaks ties.</summary>
    int QualityScore);

/// <summary>A duplicate cluster: <see cref="GroupId"/> is the lowest song id in it, and
/// <see cref="Members"/> are ranked keeper first by <see cref="IDuplicateDetectionService.RankKeeperFirst"/>.</summary>
public sealed record DuplicateGroupDto(
    int GroupId,
    /// <summary>"confirmed" when any link in the cluster is confirmed, else "suspected".</summary>
    string Confidence,
    DuplicateMemberDto Keeper,
    IReadOnlyList<DuplicateMemberDto> Members);

/// <summary>The <c>GET /api/library/duplicates</c> payload.</summary>
public sealed record DuplicateGroupsReport(
    /// <summary>Members detection has flagged <see cref="SongMetadata.IsDuplicate"/> — not cluster size.</summary>
    int TotalDuplicates,
    int Groups,
    IReadOnlyList<DuplicateGroupDto> DuplicateGroups);

/// <summary>
/// The duplicates read model: derives clusters from the persisted pairwise
/// <see cref="SongDuplicateLink"/> rows at read time (there is no group entity to keep consistent)
/// and describes each member's place in its cluster. Pure — the endpoint loads the caller's Active
/// links and the songs they reference, and this decides everything else.
/// <para>
/// Suspected links join the union-find alongside confirmed ones on purpose: a group card shows its
/// confirmed core plus any lower-confidence hangers-on, so the user can dismiss or resolve a whole
/// cluster in one place. Detection (<see cref="DuplicateDetectionService"/>) clusters over confirmed
/// links only, which is why a member's <see cref="DuplicateMemberDto.IsDuplicate"/> can be false
/// inside a group here.
/// </para>
/// </summary>
public static class DuplicateGroupProjection
{
    public static DuplicateGroupsReport Build(
        IEnumerable<SongDuplicateLink> activeLinks,
        IReadOnlyDictionary<int, SongMetadata> songsById)
    {
        // Links referencing a soft-deleted song are stale until the next detection run; skip them.
        var links = activeLinks
            .Where(l => songsById.ContainsKey(l.SongIdLow) && songsById.ContainsKey(l.SongIdHigh))
            .ToList();

        var unionFind = new SongIdUnionFind();
        foreach (var link in links)
            unionFind.Union(link.SongIdLow, link.SongIdHigh);

        var linksByCluster = links.ToLookup(l => unionFind.Find(l.SongIdLow));

        var groups = new List<DuplicateGroupDto>();
        var totalDuplicates = 0;

        var clusters = unionFind.Clusters()
            .Select(ids => (Root: unionFind.Find(ids[0]), Ids: ids))
            .OrderBy(c => c.Root);

        foreach (var cluster in clusters)
        {
            var members = cluster.Ids.Select(id => songsById[id]).ToList();
            if (members.Count < 2)
                continue;

            var clusterLinks = linksByCluster[cluster.Root].ToList();
            var confirmedIds = clusterLinks
                .Where(l => l.Confidence == DuplicateConfidence.Confirmed)
                .SelectMany(l => new[] { l.SongIdLow, l.SongIdHigh })
                .ToHashSet();

            var ranked = IDuplicateDetectionService.RankKeeperFirst(members);
            var keeper = ranked[0];
            totalDuplicates += members.Count(m => m.IsDuplicate);

            var memberDtos = ranked
                .Select(m => DescribeMember(m, keeper, clusterLinks, confirmedIds))
                .ToList();

            groups.Add(new DuplicateGroupDto(
                GroupId: cluster.Root,
                Confidence: confirmedIds.Count > 0 ? "confirmed" : "suspected",
                Keeper: memberDtos[0],
                Members: memberDtos));
        }

        return new DuplicateGroupsReport(totalDuplicates, groups.Count, groups);
    }

    private static DuplicateMemberDto DescribeMember(
        SongMetadata m,
        SongMetadata keeper,
        IReadOnlyList<SongDuplicateLink> clusterLinks,
        IReadOnlySet<int> confirmedIds)
    {
        var memberLinks = clusterLinks
            .Where(l => l.SongIdLow == m.Id || l.SongIdHigh == m.Id)
            .ToList();
        var reasons = memberLinks.Aggregate(DuplicateMatchReason.None, (acc, l) => acc | l.Reasons);
        var similarity = memberLinks.Max(l => l.Similarity);

        return new DuplicateMemberDto(
            Id: m.Id,
            SourcePath: m.SourcePath,
            FileName: m.FileName,
            Extension: m.Extension,
            FileSizeBytes: m.FileSizeBytes,
            Artist: m.Artist,
            AlbumArtist: m.AlbumArtist,
            Album: m.Album,
            Title: m.Title,
            Year: m.Year,
            TrackNumber: m.TrackNumber,
            DurationSeconds: m.DurationSeconds,
            Bitrate: m.Bitrate,
            Fingerprint: m.Fingerprint,
            IsDuplicate: m.IsDuplicate,
            DuplicateOfId: m.DuplicateOfId,
            EnrichmentStatus: m.EnrichmentStatus,
            DestinationPath: m.DestinationPath,
            IsBuilt: m.LibraryBuildStatus == LibraryBuildStatus.Done && m.DestinationPath != null,
            IsKeeper: m.Id == keeper.Id,
            IsPinned: m.DuplicateKeeperPinnedAtUtc != null,
            Confidence: confirmedIds.Contains(m.Id) ? "confirmed" : "suspected",
            Reasons: DescribeReasons(reasons),
            Similarity: similarity,
            QualityScore: IDuplicateDetectionService.QualityScore(m));
    }

    /// <summary>The wire names of a link's match reasons, in flag order. Both clients switch on these.</summary>
    public static string[] DescribeReasons(DuplicateMatchReason reasons)
    {
        var names = new List<string>(3);
        if (reasons.HasFlag(DuplicateMatchReason.ExactFingerprint)) names.Add("exact-fingerprint");
        if (reasons.HasFlag(DuplicateMatchReason.FingerprintSimilarity)) names.Add("fingerprint-similarity");
        if (reasons.HasFlag(DuplicateMatchReason.AcoustIdTrack)) names.Add("acoustid");
        if (reasons.HasFlag(DuplicateMatchReason.Isrc)) names.Add("isrc");
        if (reasons.HasFlag(DuplicateMatchReason.Metadata)) names.Add("metadata");
        return [.. names];
    }
}
