using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// The machine itself: each ingest run — a scan of the source share and the fingerprint / enrich /
/// build work it cascaded into — plus the two things that explain a change the owner did not make,
/// namely the app updating and the pipeline's own settings moving.
/// </summary>
public sealed class PipelineActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var summaries = await CollectRunsAsync(window, ct);
        summaries.AddRange(await CollectPipelineChangesAsync(window, ct));
        return summaries;
    }

    private async Task<List<HistorySummary>> CollectRunsAsync(ActivityWindow window, CancellationToken ct)
    {
        var runs = await db.IngestRuns
            .AsNoTracking()
            .Where(r => r.StartedAtUtc >= window.FromUtc && r.StartedAtUtc <= window.ToUtc)
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(r => new
            {
                r.Id, r.StartedAtUtc, r.EndedAtUtc, r.Status, r.TriggerLabel,
                r.TracksDiscovered, r.TracksProcessed, r.TracksFingerprinted, r.TracksEnriched,
                r.TracksCopied, r.TracksReview, r.TracksFailed,
            })
            .ToListAsync(ct);

        return runs.Select(r =>
        {
            var (kind, tint, headline) = r.Status switch
            {
                IngestRunStatus.Running => (
                    "scan-running", ActivityTint.Info,
                    $"Scanning the source library — {ActivityText.Plural(r.TracksDiscovered, "track")} so far"),
                IngestRunStatus.Failed => (
                    "scan-failed", ActivityTint.Error,
                    "The scan failed"),
                IngestRunStatus.Cancelled => (
                    "scan-cancelled", ActivityTint.Info,
                    $"Scan cancelled after {ActivityText.Plural(r.TracksProcessed, "track")}"),
                _ => (
                    "scan-completed", ActivityTint.Ok,
                    r.TracksDiscovered > 0
                        ? $"Scan found {ActivityText.Plural(r.TracksDiscovered, "new track")}"
                        : "Scan found nothing new"),
            };

            var parts = new List<string>();
            if (r.TracksFingerprinted > 0) parts.Add($"{r.TracksFingerprinted} fingerprinted");
            if (r.TracksEnriched > 0) parts.Add($"{r.TracksEnriched} identified");
            if (r.TracksCopied > 0) parts.Add($"{r.TracksCopied} built");
            if (r.TracksReview > 0) parts.Add($"{r.TracksReview} to review");
            if (r.TracksFailed > 0) parts.Add($"{r.TracksFailed} failed");
            if (!string.IsNullOrWhiteSpace(r.TriggerLabel)) parts.Add($"triggered by {r.TriggerLabel}");

            return ActivityText.Summary(
                ActivityCategory.Pipeline, kind, tint,
                idKey: r.Id.ToString(),
                headline: headline,
                detail: parts.Count > 0 ? string.Join(" · ", parts) : null,
                items: [new ActivityItem(null, null, null, null, r.EndedAtUtc ?? r.StartedAtUtc)],
                runId: r.Id);
        }).ToList();
    }

    /// <summary>
    /// The app updating, and the pipeline's behaviour changing under it.
    /// <para>
    /// Both are read off <see cref="EnrichmentSnapshot"/>, which already stamps the running version and
    /// a hash of the behavioural config every time a run finalizes — a new hash means a new snapshot
    /// row rather than a refresh of the existing one, so a change of either between consecutive
    /// snapshots <i>is</i> the transition. This is the answer to "why did my match rate move
    /// overnight", and it needs no new column to say it.
    /// </para>
    /// <para>
    /// The caveat worth knowing: a snapshot for an existing (version, config) is refreshed in place, so
    /// the timestamp is when that combination was last seen, not the instant it began.
    /// </para>
    /// </summary>
    private async Task<List<HistorySummary>> CollectPipelineChangesAsync(ActivityWindow window, CancellationToken ct)
    {
        // One extra snapshot from before the window, so a change on the very first in-window row is
        // still recognised as a change rather than looking like the beginning of time.
        var snapshots = await db.EnrichmentSnapshots
            .AsNoTracking()
            .Where(s => s.CapturedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.CapturedAtUtc)
            .ThenByDescending(s => s.Id)
            .Take(200)
            .Select(s => new { s.Id, s.CapturedAtUtc, s.Version, s.ConfigHash, s.ConfigJson })
            .ToListAsync(ct);

        var ordered = snapshots.OrderBy(s => s.CapturedAtUtc).ThenBy(s => s.Id).ToList();
        var summaries = new List<HistorySummary>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var (before, after) = (ordered[i - 1], ordered[i]);
            if (!window.Covers(after.CapturedAtUtc)) continue;

            if (!string.IsNullOrWhiteSpace(after.Version) && after.Version != before.Version)
            {
                summaries.Add(ActivityText.Summary(
                    ActivityCategory.Pipeline, "version-changed", ActivityTint.Info,
                    idKey: $"version|{after.Id}",
                    headline: $"MusicHoarder updated to {after.Version}",
                    detail: before.Version is null ? null : $"Was {before.Version}",
                    items: [new ActivityItem(null, null, null, null, after.CapturedAtUtc)]));
            }

            if (after.ConfigHash == before.ConfigHash) continue;

            var changes = SnapshotConfigDiff.Diff(before.ConfigJson, after.ConfigJson);
            if (changes.Count == 0) continue;

            summaries.Add(ActivityText.Summary(
                ActivityCategory.Pipeline, "settings-changed", ActivityTint.Info,
                idKey: $"config|{after.Id}",
                headline: changes.Count == 1
                    ? $"Pipeline setting changed: {Describe(changes[0])}"
                    : $"{ActivityText.Plural(changes.Count, "pipeline setting")} changed",
                detail: string.Join(" · ", changes.Take(4).Select(Describe)),
                items: [new ActivityItem(null, null, null, null, after.CapturedAtUtc)]));
        }

        return summaries;
    }

    private static string Describe(ConfigChange change)
    {
        var name = change.Key.Contains('.') ? change.Key[(change.Key.LastIndexOf('.') + 1)..] : change.Key;
        return (change.From, change.To) switch
        {
            (null, var to) => $"{name} set to {to}",
            (var from, null) => $"{name} removed (was {from})",
            var (from, to) => $"{name} {from} → {to}",
        };
    }
}
