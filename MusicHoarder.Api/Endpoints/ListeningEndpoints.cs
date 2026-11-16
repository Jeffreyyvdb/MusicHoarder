using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Listening;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The caller's listening stats. Every read goes through the per-user query filter on
/// <see cref="ListeningEvent"/>, so admin, demo and members each see only their own plays and the
/// route needs no capability: it is a GET that returns nothing of anyone else's.
/// </summary>
public static class ListeningEndpoints
{
    private static readonly TimeSpan MaxOffset = TimeSpan.FromHours(14);

    public static IEndpointRouteBuilder MapListeningEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/listening/stats", GetStats).WithName("GetListeningStats");
        return app;
    }

    internal static async Task<IResult> GetStats(
        MusicHoarderDbContext db, CancellationToken ct, string? range = "4w", int utcOffsetMinutes = 0)
    {
        if (!ListeningRanges.TryParse(range, out var parsed))
            return Results.BadRequest(new { error = "range must be one of 4w, 6m, 1y, all" });

        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);
        if (offset > MaxOffset) offset = MaxOffset;
        if (offset < -MaxOffset) offset = -MaxOffset;

        var now = DateTime.UtcNow;
        var from = parsed.FromUtc(now);

        var inRange = db.ListeningEvents.AsNoTracking();
        if (from is { } start)
            inRange = inRange.Where(e => e.PlayedAtUtc >= start);

        var events = await Project(inRange).ToListAsync(ct);

        var recent = await Project(db.ListeningEvents.AsNoTracking()
                .OrderByDescending(e => e.PlayedAtUtc).ThenByDescending(e => e.Id)
                .Take(ListeningStatsCalculator.RecentLimit))
            .ToListAsync(ct);

        var first = await db.ListeningEvents.AsNoTracking()
            .MinAsync(e => (DateTime?)e.PlayedAtUtc, ct);

        return Results.Ok(ListeningStatsCalculator.Compute(events, recent, parsed, now, offset, first));
    }

    private static IQueryable<ListeningEventRow> Project(IQueryable<ListeningEvent> source) =>
        source.Select(e => new ListeningEventRow(
            e.SongId, e.PlayedAtUtc, e.Title, e.Artist, e.AlbumArtist, e.Album, e.Genre, e.DurationSeconds));
}
