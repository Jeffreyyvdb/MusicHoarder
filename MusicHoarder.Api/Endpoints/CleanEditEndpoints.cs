using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.CleanEdits;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The clean-edit check: which built songs are a label's clean edit (or have muted words) although the
/// explicit original exists. Admin-only, and not readable by the demo account: the run touches files
/// and third-party catalogs for the instance.
/// </summary>
public static class CleanEditEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    /// <summary>The verdicts that mean "this file is not the explicit original".</summary>
    private static readonly CleanEditVerdict[] FoundVerdicts = [CleanEditVerdict.CleanMaster, CleanEditVerdict.MutedWords];

    public static IEndpointRouteBuilder MapCleanEditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/clean-edits").WithTags("CleanEdits").RequireAdmin();

        group.MapGet("", List)
            .WithName("ListCleanEdits")
            .WithSummary("Counts per verdict and the songs found to be clean edits or muted, newest check first.");
        group.MapPost("/check", Start)
            .WithName("StartCleanEditCheck")
            .WithSummary("Check every built song that has no current check. Resumes where a stopped run left off.");
        group.MapPost("/stop", Stop)
            .WithName("StopCleanEditCheck")
            .WithSummary("Stop the running check after the song it is on.");
        group.MapGet("/progress", GetProgress)
            .WithName("GetCleanEditProgress")
            .WithSummary("Progress of the running (or last) check.");

        return app;
    }

    public sealed record CleanEditReferenceDto(string Edition, string Id, double Correlation);

    public sealed record CleanEditItemDto(
        int SongId,
        string? Artist,
        string? Title,
        string? Album,
        string Verdict,
        double? Margin,
        double? EditedSeconds,
        IReadOnlyList<CleanEditReferenceDto> References,
        IReadOnlyList<double> MutedAtSeconds,
        DateTime CheckedAtUtc);

    public sealed record CleanEditListDto(
        IReadOnlyDictionary<string, int> Counts,
        int Eligible,
        int FoundTotal,
        IReadOnlyList<CleanEditItemDto> Items);

    internal static async Task<IResult> List(
        MusicHoarderDbContext db, int? limit, int? offset, CancellationToken ct)
    {
        var checks = db.CleanEditChecks.AsNoTracking().Where(c => c.Song.DeletedAtUtc == null);

        var counts = (await checks.GroupBy(c => c.Verdict)
                .Select(g => new { Verdict = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => ToWire(x.Verdict), x => x.Count);
        foreach (var verdict in Enum.GetValues<CleanEditVerdict>())
            counts.TryAdd(ToWire(verdict), 0);

        var eligible = await db.Songs.AsNoTracking().CountAsync(
            s => s.DeletedAtUtc == null && s.DestinationPath != null && s.Artist != null && s.Title != null, ct);

        var found = checks.Where(c => FoundVerdicts.Contains(c.Verdict));
        var foundTotal = await found.CountAsync(ct);
        var rows = await found
            .OrderBy(c => c.Song.Artist).ThenBy(c => c.Song.Title).ThenBy(c => c.SongId)
            .Skip(Math.Max(0, offset ?? 0))
            .Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))
            .Select(c => new
            {
                c.SongId, c.Song.Artist, c.Song.Title, c.Song.Album,
                c.Verdict, c.Margin, c.EditedSeconds, c.EvidenceJson, c.CheckedAtUtc,
            })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var evidence = CleanEditEvidence.FromJson(r.EvidenceJson);
            return new CleanEditItemDto(
                r.SongId, r.Artist, r.Title, r.Album, ToWire(r.Verdict), r.Margin, r.EditedSeconds,
                evidence?.References.Select(x => new CleanEditReferenceDto(x.Edition.ToString().ToLowerInvariant(), x.Id, x.Correlation)).ToList() ?? [],
                evidence?.MutedAtSeconds ?? [],
                r.CheckedAtUtc);
        }).ToList();

        return Results.Ok(new CleanEditListDto(counts, eligible, foundTotal, items));
    }

    private static IResult Start(CleanEditSweep sweep, ICurrentUserAccessor user) =>
        sweep.TryStart(user.UserId)
            ? Results.Accepted(value: sweep.Progress)
            : Results.Conflict(new { error = "A check is already running." });

    private static IResult Stop(CleanEditSweep sweep)
    {
        sweep.Stop();
        return Results.Ok(sweep.Progress);
    }

    private static IResult GetProgress(CleanEditSweep sweep) => Results.Ok(sweep.Progress);

    private static string ToWire(CleanEditVerdict verdict) =>
        char.ToLowerInvariant(verdict.ToString()[0]) + verdict.ToString()[1..];
}
