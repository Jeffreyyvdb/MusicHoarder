using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality.Jev;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The grader trial: the chat grader against decision models (Jev) on the same dossiers. Admin only;
/// nothing here changes a song's or album's authoritative grade.
/// </summary>
public static class QualityTrialEndpoints
{
    private const int MaxRunLimit = 500;

    public static IEndpointRouteBuilder MapQualityTrialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quality/trial").WithTags("Quality").RequireAdmin();

        group.MapGet("/status", GetStatus)
            .WithName("GetGraderTrialStatus")
            .WithSummary("Whether the grader trial is on, its decision models and settings.");

        group.MapGet("/report", GetReport)
            .WithName("GetGraderTrialReport")
            .WithSummary("Speed, price, agreement and labelled quality per grader arm, and the decision rule's checks.");

        group.MapGet("/export.csv", ExportCsv)
            .WithName("ExportGraderTrialCsv")
            .WithSummary("Every incumbent/decision pair as CSV, with human labels.");

        group.MapPost("/run", Run)
            .WithName("RunGraderTrial")
            .WithSummary("Grade up to `limit` subjects with each decision model now (pairs with existing grades; no chat calls).");

        group.MapPost("/stability", RunStability)
            .WithName("RunGraderTrialStability")
            .WithSummary("Grade up to `limit` already-answered subjects again on the same input, to measure verdict flips. `includeIncumbent=true` also re-asks the chat grader (costs chat calls).");

        group.MapGet("/labels/queue", GetLabelQueue)
            .WithName("GetGraderTrialLabelQueue")
            .WithSummary("Unlabelled pairs to label: disagreements first, plus a random sample of agreements.");

        group.MapPut("/labels", SaveLabel)
            .WithName("SaveGraderTrialLabel")
            .WithSummary("Save a human label (right / wrong / unsure) for one subject on one dossier.");

        return app;
    }

    private static IResult GetStatus(
        IDecisionClient client,
        IOptionsMonitor<JevGradingOptions> options,
        IOptionsMonitor<QualityGradingOptions> grading)
    {
        var o = options.CurrentValue;
        return Results.Ok(new
        {
            enabled = o.Enabled,
            configured = client.IsConfigured,
            models = o.ModelList,
            primaryModel = o.PrimaryModel,
            incumbentModel = grading.CurrentValue.Model,
            shadowSampleRate = o.ShadowSampleRate,
            gradeAlbums = o.GradeAlbums,
            maxStateBytes = o.MaxStateBytes,
            songQuestionVersion = JevQuestions.SongVersion,
            albumQuestionVersion = JevQuestions.AlbumVersion,
        });
    }

    private static async Task<IResult> GetReport(string? kind, GraderTrialService trial, CancellationToken ct) =>
        ParseKind(kind) is { } k ? Results.Ok(await trial.BuildReportAsync(k, ct)) : BadKind();

    private static async Task<IResult> ExportCsv(string? kind, GraderTrialService trial, CancellationToken ct)
    {
        if (ParseKind(kind) is not { } k) return BadKind();
        var csv = await trial.ExportCsvAsync(k, ct);
        return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
            $"grader-trial-{k.ToString().ToLowerInvariant()}s.csv");
    }

    private static async Task<IResult> Run(
        string? kind, int? limit, GraderTrialRunner runner, IDecisionClient client, CancellationToken ct)
    {
        if (ParseKind(kind) is not { } k) return BadKind();
        if (!client.IsConfigured) return NotConfigured();
        var result = await runner.RunAsync(k, Math.Clamp(limit ?? 50, 1, MaxRunLimit), GraderRunSource.Manual, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RunStability(
        string? kind, int? limit, bool? includeIncumbent, GraderTrialRunner runner, IDecisionClient client, CancellationToken ct)
    {
        if (ParseKind(kind) is not { } k) return BadKind();
        if (!client.IsConfigured) return NotConfigured();
        var result = await runner.RunStabilityAsync(k, Math.Clamp(limit ?? 100, 1, MaxRunLimit), includeIncumbent == true, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetLabelQueue(string? kind, int? limit, GraderTrialService trial, CancellationToken ct) =>
        ParseKind(kind) is { } k
            ? Results.Ok(await trial.LabelQueueAsync(k, Math.Clamp(limit ?? 20, 1, 100), ct))
            : BadKind();

    private static async Task<IResult> SaveLabel(TrialLabelRequest request, GraderTrialService trial, CancellationToken ct)
    {
        if (ParseKind(request.Kind) is not { } k) return BadKind();
        if (!Enum.TryParse<GraderTrialLabelValue>(request.Label, ignoreCase: true, out var value)
            || !Enum.IsDefined(value))
            return Results.BadRequest(new { error = "label must be right, wrong or unsure" });

        var saved = await trial.SaveLabelAsync(k, request, value, ct);
        return saved is null
            ? Results.NotFound(new { error = "No pair of the primary decision model and the incumbent exists for that subject and input." })
            : Results.Ok(new
            {
                saved.SubjectId,
                fingerprint = saved.InputFingerprint,
                label = saved.Label.ToString(),
                saved.UnsupportedIdentity,
                stratum = saved.Stratum.ToString(),
            });
    }

    private static GraderSubjectKind? ParseKind(string? kind) => (kind ?? "song").Trim().ToLowerInvariant() switch
    {
        "song" or "songs" => GraderSubjectKind.Song,
        "album" or "albums" => GraderSubjectKind.Album,
        _ => null,
    };

    private static IResult BadKind() => Results.BadRequest(new { error = "kind must be song or album" });

    private static IResult NotConfigured() => Results.Conflict(new
    {
        error = "The grader trial is off. It needs JevGrading:Enabled and a QualityGrading API key.",
        code = "not_configured",
    });
}
