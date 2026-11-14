using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Reports of wrong downloads (<see cref="SongReport"/>): made from a track's menu, which usually also
/// removes the track, and listed on Manage → Reported downloads as evidence for improving how
/// downloads are found. Admin-only: only the library owner downloads, and removal deletes files.
/// The list is the caller's own rows through the per-user query filter, so the demo may read it.
/// </summary>
public static class SongReportsEndpoints
{
    public static IEndpointRouteBuilder MapSongReportsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/songs/{id:int}/report", Report)
            .RequireAdmin()
            .WithName("ReportSong")
            .WithTags("Tracks")
            .WithSummary("Reports a downloaded track as wrong and, by default, removes it from the library.");

        app.MapGet("/api/song-reports", List)
            .RequireAdmin()
            .AllowDemoRead()
            .WithName("ListSongReports")
            .WithTags("Tracks")
            .WithSummary("Reported downloads, newest first: what was asked for, what arrived, and from where.");

        return app;
    }

    /// <summary><paramref name="Reason"/> is a <see cref="SongReportReason"/> name ("WrongSong", "WrongVersion", …).</summary>
    public sealed record ReportSongRequest(string? Reason, string? Note, bool? Remove);

    public sealed record SongReportDto(
        int Id,
        int? SongId,
        string Reason,
        string? Note,
        string? Title,
        string? Artist,
        string? Album,
        int? DurationMs,
        string? FileName,
        string? MatchedBy,
        double? MatchConfidence,
        int? WishlistItemId,
        string? Origin,
        string? RequestedTitle,
        string? RequestedArtist,
        string? RequestedAlbum,
        int? RequestedDurationMs,
        string? SpotifyTrackId,
        string? DeezerTrackId,
        string? Isrc,
        string? DownloadProvider,
        string? DownloadedSourceId,
        string? SourceUrl,
        DateTime? RemovedAtUtc,
        DateTime CreatedAtUtc);

    internal static async Task<IResult> Report(
        int id,
        ReportSongRequest body,
        SongReportService reports,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (!Enum.TryParse<SongReportReason>(body.Reason, ignoreCase: true, out var reason)
            || !Enum.IsDefined(reason) || int.TryParse(body.Reason, out _))
            return Results.BadRequest(new { message = "Unknown reason." });
        if (body.Note is { Length: > 1000 })
            return Results.BadRequest(new { message = "The note is limited to 1000 characters." });

        var result = await reports.ReportAsync(currentUser.UserId, id, reason, body.Note, body.Remove ?? true, ct);
        return result.Outcome switch
        {
            SongReportOutcome.Reported => Results.Ok(ToDto(result.Report!)),
            SongReportOutcome.NotFound => Results.NotFound(new { message = result.Message }),
            SongReportOutcome.NotRemovable => Results.Conflict(new { message = result.Message }),
            _ => Results.Problem(result.Message, statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    internal static async Task<IResult> List(MusicHoarderDbContext db, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 200, 1, 1000);
        var rows = await db.SongReports
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(take)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(ToDto));
    }

    internal static SongReportDto ToDto(SongReport r) => new(
        r.Id, r.SongId, r.Reason.ToString(), r.Note,
        r.Title, r.Artist, r.Album, r.DurationMs, r.FileName, r.MatchedBy, r.MatchConfidence,
        r.WishlistItemId, r.Origin?.ToString(),
        r.RequestedTitle, r.RequestedArtist, r.RequestedAlbum, r.RequestedDurationMs,
        r.SpotifyTrackId, r.DeezerTrackId, r.Isrc,
        r.DownloadProvider, r.DownloadedSourceId, r.SourceUrl,
        r.RemovedAtUtc, r.CreatedAtUtc);
}
