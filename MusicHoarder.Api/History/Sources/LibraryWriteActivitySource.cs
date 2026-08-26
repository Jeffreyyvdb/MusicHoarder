using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// What physically landed in the destination library: the append-only
/// <see cref="LibraryWriteEvent"/> rollups (tag diffs, consolidations, renames, covers) plus the
/// moment each track first reached the destination.
/// </summary>
public sealed class LibraryWriteActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var (events, titles) = await HistoryEndpoints.LoadWriteEventsAsync(db, window, ct);
        var summaries = HistoryEndpoints.RollUp(events, titles);

        // A first build also emits a tag diff against the source baseline, which the rollup labels
        // "Tags updated" — true, but it buries the thing the owner actually cares about. So the arrival
        // of a track in the destination gets its own entry.
        var built = await db.Songs
            .AsNoTracking()
            .Where(s => s.LibraryBuiltAtUtc != null
                && s.LibraryBuiltAtUtc >= window.FromUtc && s.LibraryBuiltAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.LibraryBuiltAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist,
                At = s.LibraryBuiltAtUtc!.Value,
            })
            .ToListAsync(ct);

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Written, "built", ActivityTint.Ok,
            built.Select(b => new ActivityItem(b.Id, b.Title, b.Album, b.AlbumArtist ?? b.Artist, b.At)),
            rows => $"Built {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)} into the library"));

        var failed = await db.Songs
            .AsNoTracking()
            .Where(s => s.LibraryBuildStatus == LibraryBuildStatus.Failed
                && s.LibraryBuildLastAttemptedAtUtc != null
                && s.LibraryBuildLastAttemptedAtUtc >= window.FromUtc
                && s.LibraryBuildLastAttemptedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.LibraryBuildLastAttemptedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.LibraryBuildError,
                At = s.LibraryBuildLastAttemptedAtUtc!.Value,
            })
            .ToListAsync(ct);

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Written, "build-failed", ActivityTint.Error,
            failed.Select(f => new ActivityItem(
                f.Id, f.Title, f.Album, f.AlbumArtist ?? f.Artist, f.At, ActivityText.Clip(f.LibraryBuildError))),
            rows => $"Build failed for {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
            rows => ActivityText.Clip(rows[0].Detail)));

        return summaries;
    }
}
