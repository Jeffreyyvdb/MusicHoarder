using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// The cover-art chain giving up. Successful cover writes already reach the feed as
/// <see cref="LibraryWriteEventKind.AlbumCoverWritten"/> rollups (they are real destination writes);
/// this fills in the other half — the albums every provider drew a blank on.
/// </summary>
public sealed class ArtworkActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        // AlbumCoverFetchAttempt is catalog-style — no per-user query filter — so the rows are narrowed
        // to folders this owner has actually written to. LibraryWriteEvent is the cheap way to ask:
        // it carries AlbumFolder, is indexed on it, and IS owner-filtered.
        var attempts = await db.AlbumCoverFetchAttempts
            .AsNoTracking()
            .Where(a => a.LastAttemptAtUtc >= window.FromUtc && a.LastAttemptAtUtc <= window.ToUtc)
            .OrderByDescending(a => a.LastAttemptAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(a => new { a.Id, a.AlbumFolder, a.Status, a.AttemptCount, a.LastAttemptAtUtc })
            .ToListAsync(ct);

        if (attempts.Count == 0) return [];

        var folders = attempts.Select(a => a.AlbumFolder).Distinct().ToList();
        var mine = await db.LibraryWriteEvents
            .AsNoTracking()
            .Where(e => e.AlbumFolder != null && folders.Contains(e.AlbumFolder))
            .Select(e => new { Folder = e.AlbumFolder!, e.Album, e.AlbumArtist })
            .Distinct()
            .ToListAsync(ct);
        var nameByFolder = mine
            .GroupBy(m => m.Folder)
            .ToDictionary(g => g.Key, g => g.First());

        var summaries = new List<HistorySummary>();
        foreach (var group in attempts
            .Where(a => nameByFolder.ContainsKey(a.AlbumFolder))
            .GroupBy(a => (a.Status, Day: a.LastAttemptAtUtc.Date)))
        {
            var rows = group.OrderByDescending(a => a.LastAttemptAtUtc).ToList();
            var first = nameByFolder[rows[0].AlbumFolder];
            var notFound = group.Key.Status == AlbumCoverFetchStatus.NotFound;
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Artwork,
                notFound ? "cover-not-found" : "cover-fetch-failed",
                notFound ? ActivityTint.Info : ActivityTint.Warn,
                idKey: $"{group.Key.Status}|{group.Key.Day:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: rows.Count == 1
                    ? $"No cover art anywhere for {ActivityText.Quote(first.Album)}"
                    : $"No cover art found for {ActivityText.Plural(rows.Count, "album")}",
                detail: notFound
                    ? "Every artwork provider answered, none had it"
                    : "A provider errored or rate-limited — MusicHoarder will retry",
                items: rows.Select(r =>
                {
                    var name = nameByFolder[r.AlbumFolder];
                    return new ActivityItem(null, null, name.Album, name.AlbumArtist, r.LastAttemptAtUtc,
                        r.AttemptCount > 1 ? $"{r.AttemptCount} attempts so far" : null);
                }).ToList()));
        }

        return summaries;
    }
}
