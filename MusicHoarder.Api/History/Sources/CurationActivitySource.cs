using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// Tidying the library: merges and splits from the dedup tools, duplicate pairs the detector found,
/// AI quality grades, and tracks that vanished from the source share.
/// </summary>
public sealed class CurationActivitySource(MusicHoarderDbContext db, IDedupActionHistory dedupHistory)
    : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var summaries = new List<HistorySummary>();
        summaries.AddRange(await CollectDedupActionsAsync(window, ct));
        summaries.AddRange(await CollectDuplicatesAsync(window, ct));
        summaries.AddRange(await CollectRemovalsAsync(window, ct));
        summaries.AddRange(await CollectGradesAsync(window, ct));
        return summaries;
    }

    /// <summary>
    /// The merges, splits and heals the dedup tools performed. Reuses the Inbox's own history service
    /// rather than re-deriving the batches, so the two surfaces can never disagree about what happened.
    /// </summary>
    private async Task<List<HistorySummary>> CollectDedupActionsAsync(ActivityWindow window, CancellationToken ct)
    {
        var actions = await dedupHistory.ListAsync(take: 200, ct);
        return actions
            .Where(a => window.Covers(a.CreatedAtUtc))
            .Select(a => ActivityText.Summary(
                ActivityCategory.Curation, DedupKind(a.Source), a.Reverted ? ActivityTint.Info : ActivityTint.Ok,
                idKey: $"{a.Source}|{a.BatchTicks}",
                headline: $"{DedupLabel(a.Source)} — {ActivityText.Plural(a.SongCount, "track")} updated"
                    + (a.Reverted ? " (since reverted)" : ""),
                detail: a.Highlights.Count > 0 ? string.Join(" · ", a.Highlights) : null,
                items: [new ActivityItem(null, null, null, null, a.CreatedAtUtc)]))
            .ToList();
    }

    private static string DedupKind(string source) => source switch
    {
        "artist-merge" => "artists-merged",
        "album-merge" => "albums-merged",
        "artist-credit-split" => "credit-split",
        _ => "album-healed",
    };

    private static string DedupLabel(string source) => source switch
    {
        "artist-merge" => "Merged two spellings of an artist",
        "album-merge" => "Merged two versions of an album",
        "artist-credit-split" => "Split a combined artist credit",
        _ => "Healed an album split across artist spellings",
    };

    private async Task<List<HistorySummary>> CollectDuplicatesAsync(ActivityWindow window, CancellationToken ct)
    {
        var links = await db.SongDuplicateLinks
            .AsNoTracking()
            .Where(l => (l.DetectedAtUtc >= window.FromUtc && l.DetectedAtUtc <= window.ToUtc)
                || (l.DismissedAtUtc != null
                    && l.DismissedAtUtc >= window.FromUtc && l.DismissedAtUtc <= window.ToUtc))
            .OrderByDescending(l => l.DetectedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(l => new { l.Id, l.SongIdLow, l.SongIdHigh, l.DetectedAtUtc, l.DismissedAtUtc, l.Status, l.Confidence })
            .ToListAsync(ct);

        if (links.Count == 0) return [];

        // A duplicate pair only means something once you can see which two tracks it is about, so the
        // expanded row names them; without this it reads "Duplicates found" and says nothing.
        var pairIds = links.SelectMany(l => new[] { l.SongIdLow, l.SongIdHigh }).Distinct().ToList();
        var titles = await db.Songs
            .AsNoTracking()
            .Where(s => pairIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.Album })
            .ToListAsync(ct);
        var titleById = titles.ToDictionary(t => t.Id, t => $"{t.Title} ({t.Album})");
        string Pair(int low, int high) =>
            $"{titleById.GetValueOrDefault(low, $"#{low}")} · {titleById.GetValueOrDefault(high, $"#{high}")}";

        var summaries = new List<HistorySummary>();

        foreach (var group in links
            .Where(l => window.Covers(l.DetectedAtUtc) && l.Status == DuplicateLinkStatus.Active)
            .GroupBy(l => l.DetectedAtUtc.Date))
        {
            var rows = group.ToList();
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Curation, "duplicates-found", ActivityTint.Warn,
                idKey: $"found|{group.Key:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: $"Spotted {ActivityText.Plural(rows.Count, "possible duplicate")} in the library",
                detail: "Waiting for you in the Inbox",
                items: rows
                    .Select(r => new ActivityItem(
                        r.SongIdLow, null, null, null, r.DetectedAtUtc, Pair(r.SongIdLow, r.SongIdHigh)))
                    .ToList()));
        }

        foreach (var group in links
            .Where(l => window.Covers(l.DismissedAtUtc))
            .GroupBy(l => l.DismissedAtUtc!.Value.Date))
        {
            var rows = group.ToList();
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Curation, "duplicates-dismissed", ActivityTint.Info,
                idKey: $"dismissed|{group.Key:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: $"You dismissed {ActivityText.Plural(rows.Count, "duplicate pair")}",
                detail: null,
                items: rows
                    .Select(r => new ActivityItem(
                        r.SongIdLow, null, null, null, r.DismissedAtUtc!.Value, Pair(r.SongIdLow, r.SongIdHigh)))
                    .ToList()));
        }

        return summaries;
    }

    /// <summary>Tracks whose file stopped being on the source share — soft-deleted, never destroyed.</summary>
    private async Task<List<HistorySummary>> CollectRemovalsAsync(ActivityWindow window, CancellationToken ct)
    {
        var removed = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc != null
                && s.DeletedAtUtc >= window.FromUtc && s.DeletedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.DeletedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist,
                At = s.DeletedAtUtc!.Value,
            })
            .ToListAsync(ct);

        return ActivityText.GroupByAlbumDay(
            ActivityCategory.Curation, "track-removed", ActivityTint.Warn,
            removed.Select(r => new ActivityItem(r.Id, r.Title, r.Album, r.AlbumArtist ?? r.Artist, r.At)),
            g => $"{ActivityText.Plural(g.Count, "track")} of {ActivityText.Quote(g[0].Album)} left the source library",
            _ => "The rows are kept — nothing is ever deleted outright").ToList();
    }

    private async Task<List<HistorySummary>> CollectGradesAsync(ActivityWindow window, CancellationToken ct)
    {
        var grades = await db.SongQualityGrades
            .AsNoTracking()
            .Where(g => g.GradedAtUtc >= window.FromUtc && g.GradedAtUtc <= window.ToUtc)
            .OrderByDescending(g => g.GradedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(g => new
            {
                g.Id, g.SongId, g.Verdict, g.Score, g.GradedAtUtc,
                Title = g.Song.Title,
                Album = g.Song.Album,
                AlbumArtist = g.Song.AlbumArtist,
                Artist = g.Song.Artist,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        // A grading pass covers the whole library, so the interesting half is the verdicts that ask for
        // a human. Everything else rolls into one "graded N tracks" line per album per day.
        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Curation, "graded", ActivityTint.Info,
            grades
                .Where(g => g.Verdict is not (SongQualityVerdict.Wrong or SongQualityVerdict.Questionable))
                .Select(g => new ActivityItem(
                    g.SongId, g.Title, g.Album, g.AlbumArtist ?? g.Artist, g.GradedAtUtc, $"{g.Score}/100")),
            rows => $"Graded {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
            rows => $"Average {ActivityText.Round(rows.Average(r => ParseScore(r.Detail)))}/100"));

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Curation, "graded-poorly", ActivityTint.Warn,
            grades
                .Where(g => g.Verdict is SongQualityVerdict.Wrong or SongQualityVerdict.Questionable)
                .Select(g => new ActivityItem(
                    g.SongId, g.Title, g.Album, g.AlbumArtist ?? g.Artist, g.GradedAtUtc,
                    $"{g.Verdict} · {g.Score}/100")),
            rows => $"The grader flagged {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
            _ => "Metadata looks wrong or unverified"));

        return summaries;
    }

    private static double ParseScore(string? detail) =>
        detail is not null && int.TryParse(detail.Split('/')[0], out var score) ? score : 0;
}
