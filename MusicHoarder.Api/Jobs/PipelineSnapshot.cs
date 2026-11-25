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
        var (counts, _) = await ComputeOverviewCountsAsync(active, ct);
        return counts;
    }

    public static async Task<(PipelineCounts Counts, DateTime? NewestIndexedAtUtc)> ComputeOverviewCountsAsync(
        IQueryable<SongMetadata> active,
        CancellationToken ct = default)
    {
        // Single table scan using stored HasFingerprint (avoids TOAST detoasting on Fingerprint).
        var row = await active
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Fingerprinted = g.Count(s => s.HasFingerprint && s.DurationSeconds != null),
                Enriched = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Matched || s.EnrichmentStatus == EnrichmentStatus.NeedsReview),
                BuildEligible = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Matched),
                Copied = g.Count(s =>
                    s.LibraryBuildStatus == LibraryBuildStatus.Copied ||
                    s.LibraryBuildStatus == LibraryBuildStatus.Tagged ||
                    s.LibraryBuildStatus == LibraryBuildStatus.Done),
                Review = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview),
                Failed = g.Count(s =>
                    s.EnrichmentStatus == EnrichmentStatus.Failed ||
                    s.LibraryBuildStatus == LibraryBuildStatus.Failed),
                NewestIndexedAtUtc = g.Max(s => (DateTime?)s.IndexedAtUtc),
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
            return (new PipelineCounts(0, 0, 0, 0, 0, 0, 0, 0), null);

        return (new PipelineCounts(
            Discovered: row.Total,
            Processed: row.Total,
            Fingerprinted: row.Fingerprinted,
            Enriched: row.Enriched,
            BuildEligible: row.BuildEligible,
            Copied: row.Copied,
            Review: row.Review,
            Failed: row.Failed), row.NewestIndexedAtUtc);
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
        var noTrack = active.AsNoTracking();

        var failed = await noTrack
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Failed || s.LibraryBuildStatus == LibraryBuildStatus.Failed)
            .OrderByDescending(s => s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.LibraryBuildLastAttemptedAtUtc ?? s.IndexedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var review = await noTrack
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            .OrderByDescending(s => s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        // Split AcquiredAtUtc != null from legacy null rows so the primary query uses the
        // (OwnerUserId, DeletedAtUtc, AcquiredAtUtc) index without a COALESCE full-table sort.
        var inLibrary = await noTrack
            .Where(s => s.DestinationPath != null && s.AcquiredAtUtc != null)
            .OrderByDescending(s => s.AcquiredAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var inLibraryLegacy = await noTrack
            .Where(s => s.DestinationPath != null && s.AcquiredAtUtc == null)
            .OrderByDescending(s => s.IndexedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var matched = await noTrack
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched
                && s.DestinationPath == null && s.PreviousDestinationPath == null && s.EnrichedAtUtc != null)
            .OrderByDescending(s => s.EnrichedAtUtc)
            .Take(CandidateCap).Select(ToFacts).ToListAsync(ct);

        var candidates = failed.Concat(review).Concat(inLibrary).Concat(inLibraryLegacy).Concat(matched)
            .GroupBy(f => f.Id).Select(g => g.First());

        return RecentActivity.Derive(candidates, take);
    }
}
