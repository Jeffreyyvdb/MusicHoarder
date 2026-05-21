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

public record PipelineActivity(
    string Id,
    string Type,
    string Track,
    string Artist,
    string Time,
    DateTime ActivityAt);

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

    public static async Task<List<PipelineActivity>> ComputeRecentActivityAsync(
        IQueryable<SongMetadata> active,
        int take,
        DateTime now,
        CancellationToken ct = default)
    {
        var recentSongs = await active
            .OrderByDescending(s => s.LibraryBuiltAtUtc ?? s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc)
            .Take(take)
            .Select(s => new
            {
                s.Id,
                s.FileName,
                s.Artist,
                s.IndexedAtUtc,
                s.EnrichedAtUtc,
                s.EnrichmentLastAttemptedAtUtc,
                s.LibraryBuiltAtUtc,
                s.LibraryBuildLastAttemptedAtUtc,
                s.EnrichmentStatus,
                s.LibraryBuildStatus,
            })
            .ToListAsync(ct);

        return recentSongs.Select(s =>
        {
            string type;
            DateTime activityAt;
            if (s.LibraryBuildStatus is LibraryBuildStatus.Copied or LibraryBuildStatus.Tagged or LibraryBuildStatus.Done
                && s.LibraryBuiltAtUtc.HasValue)
            {
                type = "copied";
                activityAt = s.LibraryBuiltAtUtc.Value;
            }
            else if (s.EnrichmentStatus == EnrichmentStatus.Failed || s.LibraryBuildStatus == LibraryBuildStatus.Failed)
            {
                type = "failed";
                activityAt = s.EnrichedAtUtc ?? s.LibraryBuildLastAttemptedAtUtc ?? s.IndexedAtUtc;
            }
            else if (s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            {
                type = "review";
                activityAt = s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc;
            }
            else if (s.EnrichmentStatus == EnrichmentStatus.Matched && s.EnrichedAtUtc.HasValue)
            {
                type = "enriched";
                activityAt = s.EnrichedAtUtc.Value;
            }
            else
            {
                type = "discovered";
                activityAt = s.IndexedAtUtc;
            }

            var diff = now - activityAt;
            var timeAgo = diff.TotalMinutes < 1 ? "just now"
                : diff.TotalMinutes < 60 ? $"{(int)diff.TotalMinutes} min ago"
                : diff.TotalHours < 24 ? $"{(int)diff.TotalHours} hr ago"
                : $"{(int)diff.TotalDays} day{(diff.TotalDays >= 2 ? "s" : "")} ago";

            return new PipelineActivity(
                Id: $"act-{s.Id}",
                Type: type,
                Track: s.FileName ?? "Unknown",
                Artist: s.Artist ?? "Unknown",
                Time: timeAgo,
                ActivityAt: activityAt);
        }).OrderByDescending(a => a.ActivityAt).ToList();
    }
}
