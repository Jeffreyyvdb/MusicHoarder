using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// New releases of followed artists, as the monitor decided them. Derived from the
/// <see cref="FollowedArtistRelease"/> rows the monitor keeps anyway (its seen set), so it costs the
/// monitor nothing; the baseline and back-fill decisions are bookkeeping, not news, and are left out.
/// </summary>
public sealed class FollowedArtistActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var rows = await db.FollowedArtistReleases
            .AsNoTracking()
            .Where(r => r.DecidedAtUtc >= window.FromUtc && r.DecidedAtUtc <= window.ToUtc
                && FollowedReleaseDecisions.News.Contains(r.Decision))
            .OrderByDescending(r => r.DecidedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(r => new { r.Id, r.Title, r.Decision, r.TracksQueued, r.DecidedAtUtc, Artist = r.FollowedArtist.Name })
            .ToListAsync(ct);

        return rows.Select(r => ActivityText.Summary(
                ActivityCategory.Acquired,
                "new-release",
                r.Decision == FollowedReleaseDecision.Queued ? ActivityTint.Ok : ActivityTint.Info,
                idKey: $"release|{r.Id}",
                headline: $"New release from {r.Artist}: {ActivityText.Quote(r.Title)}",
                detail: r.Decision switch
                {
                    FollowedReleaseDecision.Queued => $"Queued {ActivityText.Plural(r.TracksQueued, "track")} for download",
                    FollowedReleaseDecision.AlreadyOwned => "Every track was already in your library or on its way",
                    _ => "Noticed only — automatic downloads are off for this artist",
                },
                items: [new ActivityItem(null, null, r.Title, r.Artist, r.DecidedAtUtc)]))
            .ToList();
    }
}
