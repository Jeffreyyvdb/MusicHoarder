using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Counts for the Inbox's rows that would otherwise cost a full report each. The reports stay where
/// they are (<see cref="ArtistDedupEndpoints"/>, <see cref="AlbumDedupEndpoints"/>, the
/// split-albums route, <see cref="SongsEndpoints"/>, <see cref="QualityEndpoints"/>) for the pages
/// that list them; these are for the badges, which are on every page.
/// </summary>
public static class InboxCountsEndpoints
{
    public sealed record InboxDecisionCounts(int Dupes, int Ai);

    public static IEndpointRouteBuilder MapInboxCountsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inbox/name-counts", NameCounts)
            .WithName("GetInboxNameCounts")
            .WithSummary("How many suggestions the Inbox's Artist names queue (spelling clusters plus combined credits) and Album names queue (split albums plus duplicate-name pairs) hold, without the reports behind them.")
            .WithTags("Library").RequireAdmin();

        app.MapGet("/api/inbox/decision-counts", DecisionCounts)
            .WithName("GetInboxDecisionCounts")
            .WithSummary("How many items the Inbox's Duplicate tracks and AI flagged queues hold, without loading the duplicate report or quality overview.")
            .WithTags("Library").RequireAdmin().AllowDemoRead();

        return app;
    }

    internal static async Task<IResult> NameCounts(
        IInboxNameCounter counter, ICurrentUserAccessor currentUser, CancellationToken ct)
    {
        var counts = await counter.CountAsync(currentUser.UserId, ct);
        return Results.Ok(counts);
    }

    internal static async Task<IResult> DecisionCounts(
        MusicHoarderDbContext db, CancellationToken ct)
    {
        var activeLinks = await db.SongDuplicateLinks
            .AsNoTracking()
            .Where(l => l.Status == DuplicateLinkStatus.Active)
            .Select(l => new { l.SongIdLow, l.SongIdHigh })
            .ToListAsync(ct);

        var dupes = 0;
        if (activeLinks.Count > 0)
        {
            var linkedSongIds = new HashSet<int>(activeLinks.Count * 2);
            foreach (var link in activeLinks)
            {
                linkedSongIds.Add(link.SongIdLow);
                linkedSongIds.Add(link.SongIdHigh);
            }

            var liveIds = (await db.Songs
                .AsNoTracking()
                .Where(s => linkedSongIds.Contains(s.Id) && s.DeletedAtUtc == null)
                .Select(s => s.Id)
                .ToListAsync(ct))
                .ToHashSet();

            var uf = new SongIdUnionFind();
            var unioned = new HashSet<int>();
            foreach (var link in activeLinks)
            {
                if (liveIds.Contains(link.SongIdLow) && liveIds.Contains(link.SongIdHigh))
                {
                    uf.Union(link.SongIdLow, link.SongIdHigh);
                    unioned.Add(link.SongIdLow);
                    unioned.Add(link.SongIdHigh);
                }
            }

            var clusterSizes = new Dictionary<int, int>();
            foreach (var id in unioned)
            {
                var root = uf.Find(id);
                clusterSizes[root] = clusterSizes.GetValueOrDefault(root) + 1;
            }

            foreach (var size in clusterSizes.Values)
            {
                if (size >= 2) dupes++;
            }
        }

        var ai = await db.SongQualityGrades
            .AsNoTracking()
            .LatestPerSong()
            .Where(g => g.Verdict == SongQualityVerdict.Wrong || g.Verdict == SongQualityVerdict.Questionable)
            .Join(
                db.Songs.AsNoTracking().Where(s => s.DeletedAtUtc == null),
                g => g.SongId,
                s => s.Id,
                (g, s) => g.SongId)
            .CountAsync(ct);

        return Results.Ok(new InboxDecisionCounts(dupes, ai));
    }
}
