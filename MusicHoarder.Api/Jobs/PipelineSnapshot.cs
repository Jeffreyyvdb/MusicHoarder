using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Jobs;

public record PipelineCounts(
    int Discovered,
    int Processed,
    int Fingerprinted,
    int Enriched,
    int BuildEligible,
    int Copied,
    int Review,
    int Failed);

/// <summary>
/// Shared pipeline aggregate queries. Callers pass an already-scoped <c>IQueryable</c> of active
/// (non-deleted) songs — the dashboard passes the user-filtered <c>db.Songs</c>, the run monitor
/// passes an owner-scoped <c>IgnoreQueryFilters()</c> query. This keeps the per-stage count and
/// recent-activity logic in one place so the live overview and persisted runs always agree.
/// </summary>
public static class PipelineSnapshot
{
    public static async Task<PipelineCounts> ComputeCountsAsync(
        IQueryable<SongMetadata> active,
        CancellationToken ct = default)
    {
        var totalCount = await active.CountAsync(ct);
        var fingerprintedCount = await active.CountAsync(
            s => s.Fingerprint != null && s.Fingerprint != string.Empty && s.DurationSeconds != null, ct);
        var enrichedCount = await active.CountAsync(
            s => s.EnrichmentStatus == EnrichmentStatus.Matched || s.EnrichmentStatus == EnrichmentStatus.NeedsReview, ct);
        var buildEligibleCount = await active.CountAsync(s => s.EnrichmentStatus == EnrichmentStatus.Matched, ct);
        var copiedCount = await active.CountAsync(
            s => s.LibraryBuildStatus == LibraryBuildStatus.Copied ||
                 s.LibraryBuildStatus == LibraryBuildStatus.Tagged ||
                 s.LibraryBuildStatus == LibraryBuildStatus.Done, ct);
        var reviewCount = await active.CountAsync(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview, ct);
        var failedCount = await active.CountAsync(
            s => s.EnrichmentStatus == EnrichmentStatus.Failed || s.LibraryBuildStatus == LibraryBuildStatus.Failed, ct);

        return new PipelineCounts(
            Discovered: totalCount,
            Processed: totalCount,
            Fingerprinted: fingerprintedCount,
            Enriched: enrichedCount,
            BuildEligible: buildEligibleCount,
            Copied: copiedCount,
            Review: reviewCount,
            Failed: failedCount);
    }

    /// <summary>Rows read per source stamp before deriving; far more than the few dozen events shown.</summary>
    private const int CandidateCap = 200;

    private static readonly System.Linq.Expressions.Expression<Func<SongMetadata, RecentActivityFacts>> ToFacts =
        s => new RecentActivityFacts(
            s.Id, s.Title, s.Artist, s.AlbumArtist, s.Album, s.FileName,
            s.AcquiredAtUtc, s.IndexedAtUtc, s.EnrichedAtUtc, s.EnrichmentLastAttemptedAtUtc,
            s.LibraryBuiltAtUtc, s.LibraryBuildLastAttemptedAtUtc,
            s.EnrichmentStatus, s.LibraryBuildStatus,
            s.DestinationPath != null,
            s.DestinationPath != null || s.PreviousDestinationPath != null);

    public static async Task<IReadOnlyList<RecentActivityEvent>> ComputeRecentActivityAsync(
        IQueryable<SongMetadata> active,
        int take,
        CancellationToken ct = default)
    {
        var failed = await active
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Failed || s.LibraryBuildStatus == LibraryBuildStatus.Failed)
            .OrderByDescending(s => s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.LibraryBuildLastAttemptedAtUtc ?? s.IndexedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var review = await active
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            .OrderByDescending(s => s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var inLibrary = await active
            .Where(s => s.DestinationPath != null)
            .OrderByDescending(s => s.AcquiredAtUtc ?? s.IndexedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var matched = await active
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched
                && s.DestinationPath == null && s.PreviousDestinationPath == null && s.EnrichedAtUtc != null)
            .OrderByDescending(s => s.EnrichedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var candidates = failed.Concat(review).Concat(inLibrary).Concat(matched)
            .GroupBy(f => f.Id).Select(g => g.First());

        return RecentActivity.Derive(candidates, take);
    }
}
