using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// Pushing the library to the other MusicHoarder instance.
/// <para>
/// Navidrome star mirroring is not represented: it stores no timestamp of its own
/// (<see cref="SongMetadata.LikeLastSyncedValue"/> is a three-way-merge base, not a stamp), so there
/// is nothing to derive an entry from without adding a column.
/// </para>
/// </summary>
public sealed class SyncActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var states = await db.TrackSyncStates
            .AsNoTracking()
            .Where(t => t.UpdatedAtUtc >= window.FromUtc && t.UpdatedAtUtc <= window.ToUtc
                && (t.Status == TrackSyncStatus.Synced
                    || t.Status == TrackSyncStatus.Failed
                    || t.Status == TrackSyncStatus.SkippedRemoteBetter))
            .OrderByDescending(t => t.UpdatedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(t => new
            {
                t.Id, t.SongId, t.Status, t.LastError, t.Attempts, t.UpdatedAtUtc,
                Title = t.Song != null ? t.Song.Title : null,
                Album = t.Song != null ? t.Song.Album : null,
                AlbumArtist = t.Song != null ? t.Song.AlbumArtist : null,
                Artist = t.Song != null ? t.Song.Artist : null,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        foreach (var group in states.GroupBy(s => (s.Status, Day: s.UpdatedAtUtc.Date)))
        {
            var rows = group.OrderByDescending(s => s.UpdatedAtUtc).ToList();
            var (kind, tint, headline) = group.Key.Status switch
            {
                TrackSyncStatus.Synced => (
                    "synced", ActivityTint.Ok,
                    $"Pushed {ActivityText.Plural(rows.Count, "track")} to the other instance"),
                TrackSyncStatus.SkippedRemoteBetter => (
                    "sync-skipped", ActivityTint.Info,
                    $"Left {ActivityText.Plural(rows.Count, "track")} alone — the other instance has a better copy"),
                _ => (
                    "sync-failed", ActivityTint.Warn,
                    $"Sync failed for {ActivityText.Plural(rows.Count, "track")}"),
            };
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Sync, kind, tint,
                idKey: $"{group.Key.Status}|{group.Key.Day:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: headline,
                detail: ActivityText.Clip(rows[0].LastError),
                items: rows.Select(r => new ActivityItem(
                    r.SongId, r.Title, r.Album, r.AlbumArtist ?? r.Artist, r.UpdatedAtUtc,
                    ActivityText.Clip(r.LastError))).ToList()));
        }

        return summaries;
    }
}
