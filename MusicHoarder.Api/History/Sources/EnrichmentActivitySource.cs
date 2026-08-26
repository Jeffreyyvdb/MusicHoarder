using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// Working out what a track actually is: the providers agreeing on a match, a track landing in review
/// because they did not, and the owner settling it by hand.
/// </summary>
public sealed class EnrichmentActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var enriched = await db.Songs
            .AsNoTracking()
            .Where(s => s.EnrichedAtUtc != null
                && s.EnrichedAtUtc >= window.FromUtc && s.EnrichedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.EnrichedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.EnrichmentStatus, s.MatchConfidence,
                s.EnrichmentError, s.ManuallyApprovedAtUtc,
                At = s.EnrichedAtUtc!.Value,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        // A manual approval overwrites EnrichedAtUtc too, so approvals are pulled out first — otherwise
        // the owner's own decision would be reported back to them as "matched automatically".
        var approvedIds = enriched
            .Where(s => window.Covers(s.ManuallyApprovedAtUtc))
            .Select(s => s.Id)
            .ToHashSet();

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Enriched, "matched", ActivityTint.Ok,
            enriched
                .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched && !approvedIds.Contains(s.Id))
                .Select(s => new ActivityItem(
                    s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At, ConfidenceOf(s.MatchConfidence))),
            rows => $"Identified {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}"));

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Enriched, "needs-review", ActivityTint.Warn,
            enriched
                .Where(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
                .Select(s => new ActivityItem(s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At)),
            rows => $"{ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}"
                + (rows.Count == 1 ? " needs a decision" : " need a decision"),
            _ => "The providers did not agree — waiting for you in Review"));

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Enriched, "enrich-failed", ActivityTint.Error,
            enriched
                .Where(s => s.EnrichmentStatus == EnrichmentStatus.Failed)
                .Select(s => new ActivityItem(
                    s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At, ActivityText.Clip(s.EnrichmentError))),
            rows => $"Could not identify {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
            rows => ActivityText.Clip(rows[0].Detail)));

        // Approvals are keyed on their own stamp, which survives even when a later re-enrichment moves
        // EnrichedAtUtc out of the window.
        var approved = await db.Songs
            .AsNoTracking()
            .Where(s => s.ManuallyApprovedAtUtc != null
                && s.ManuallyApprovedAtUtc >= window.FromUtc && s.ManuallyApprovedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.ManuallyApprovedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist,
                At = s.ManuallyApprovedAtUtc!.Value,
            })
            .ToListAsync(ct);

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Enriched, "review-approved", ActivityTint.Ok,
            approved.Select(s => new ActivityItem(s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At)),
            rows => $"You approved {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}"));

        return summaries;
    }

    private static string? ConfidenceOf(double? confidence) =>
        confidence is { } c ? $"{ActivityText.Round(c * 100)}% confidence" : null;
}
