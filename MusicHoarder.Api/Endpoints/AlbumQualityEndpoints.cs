using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// AI grading for album reconciliations — does the canonical album we linked actually match the
/// user's local files? Mirrors <see cref="QualityEndpoints"/>; per-album routes are keyed by
/// artist+album (normalized to the canonical album, like the tracklist route).
/// </summary>
public static class AlbumQualityEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAlbumQualityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums/quality/overview", GetOverview)
            .WithName("GetAlbumQualityOverview")
            .WithSummary("Library-wide album-reconciliation grade rollup: verdict counts, average score, worst-offender albums.")
            .WithTags("Quality").RequireOwner();

        app.MapPost("/api/albums/quality/grade", GradeAlbum)
            .WithName("GradeAlbumQuality")
            .WithSummary("Grade one album now (forces a fresh LLM call).")
            .WithTags("Quality").RequireOwner();

        app.MapPost("/api/albums/quality/grade-all", GradeAll)
            .WithName("GradeAllAlbumQuality")
            .WithSummary("Enqueue every linked album for reconciliation grading (skips unchanged).")
            .WithTags("Quality").RequireOwner();

        app.MapPost("/api/albums/quality/grade-outdated", GradeOutdated)
            .WithName("GradeOutdatedAlbumQuality")
            .WithSummary("Enqueue only albums whose latest grade is outdated (prompt version or model changed since it was graded).")
            .WithTags("Quality").RequireOwner();

        app.MapGet("/api/albums/quality/progress", GetProgress)
            .WithName("GetAlbumQualityProgress")
            .WithSummary("Current album grading run progress.")
            .WithTags("Quality").RequireOwner();

        app.MapGet("/api/albums/quality/export", ExportAlbum)
            .WithName("ExportAlbumQuality")
            .WithSummary("Download the dossier + grade for one album (for feeding to an agent).")
            .WithTags("Quality").RequireOwner();

        return app;
    }

    /// <summary>Latest grade per album for the current owner ("no newer grade exists"; owner-filtered context).</summary>
    private static IQueryable<CanonicalAlbumQualityGrade> LatestGrades(MusicHoarderDbContext db) =>
        db.CanonicalAlbumQualityGrades.Where(g =>
            !db.CanonicalAlbumQualityGrades.Any(g2 => g2.CanonicalAlbumId == g.CanonicalAlbumId && g2.GradedAtUtc > g.GradedAtUtc));

    private record GradeRowDto(
        int CanonicalAlbumId, string? Artist, string? Album, int? Year,
        int Score, SongQualityVerdict Verdict, string? Summary, string? IssuesJson,
        int OwnedTrackCount, int CanonicalTrackCount, DateTime GradedAtUtc,
        int PromptVersion, string? Model);

    private static async Task<List<GradeRowDto>> LoadGradeRowsAsync(MusicHoarderDbContext db, CancellationToken ct) =>
        await LatestGrades(db)
            .Join(db.CanonicalAlbums, g => g.CanonicalAlbumId, a => a.Id,
                (g, a) => new GradeRowDto(
                    a.Id, a.DisplayArtist, a.DisplayTitle, a.Year,
                    g.Score, g.Verdict, g.Summary, g.IssuesJson,
                    g.OwnedTrackCount, g.CanonicalTrackCount, g.GradedAtUtc,
                    g.PromptVersion, g.Model))
            .ToListAsync(ct);

    /// <summary>A grade is "outdated" when the album-grading prompt version or the configured model has
    /// changed since the grade was produced — surfaced (not auto-regraded) for an explicit refresh.</summary>
    private static bool IsGradeOutdated(int promptVersion, string? model, string currentModel) =>
        promptVersion != AlbumGradingPrompt.Version
        || !string.Equals(model, currentModel, StringComparison.Ordinal);

    private static object ToAlbumRow(GradeRowDto r, string currentModel) => new
    {
        canonicalAlbumId = r.CanonicalAlbumId,
        artist = r.Artist,
        album = r.Album,
        year = r.Year,
        score = r.Score,
        verdict = r.Verdict.ToString(),
        summary = r.Summary,
        issues = ParseIssues(r.IssuesJson),
        ownedTrackCount = r.OwnedTrackCount,
        canonicalTrackCount = r.CanonicalTrackCount,
        gradedAtUtc = r.GradedAtUtc,
        isOutdated = IsGradeOutdated(r.PromptVersion, r.Model, currentModel),
    };

    internal static async Task<IResult> GetOverview(
        MusicHoarderDbContext db, IOptionsMonitor<QualityGradingOptions> options, CancellationToken ct)
    {
        var rows = await LoadGradeRowsAsync(db, ct);
        var currentModel = options.CurrentValue.Model;
        var fetchedTotal = await db.CanonicalAlbums.CountAsync(a => a.Status == CanonicalAlbumStatus.Fetched, ct);
        var agg = QualityRollup.Aggregate(rows.Select(r => new QualityRollup.GradeRow(r.Verdict, r.Score, r.IssuesJson)));

        var worst = rows
            .OrderBy(r => (int)r.Verdict)
            .ThenBy(r => r.Score)
            .ThenByDescending(r => r.GradedAtUtc)
            .Take(100)
            .Select(r => ToAlbumRow(r, currentModel))
            .ToList();

        var outdatedCount = rows.Count(r => IsGradeOutdated(r.PromptVersion, r.Model, currentModel));

        return Results.Ok(new
        {
            gradeableTotal = fetchedTotal,
            coverage = fetchedTotal == 0 ? 0d : Math.Round((double)rows.Count / fetchedTotal, 3),
            outdatedCount,
            library = new
            {
                graded = agg.Graded,
                averageScore = agg.AverageScore,
                verdicts = new
                {
                    excellent = agg.Verdicts.Excellent,
                    good = agg.Verdicts.Good,
                    questionable = agg.Verdicts.Questionable,
                    wrong = agg.Verdicts.Wrong,
                    ungradeable = agg.Verdicts.Ungradeable,
                },
                topIssues = agg.TopIssues.Select(i => new { code = i.Code, count = i.Count }),
            },
            wrongCount = agg.Verdicts.Wrong,
            worstOffenders = worst,
        });
    }

    private static async Task<IResult> GradeAlbum(
        string artist, string album, MusicHoarderDbContext db, IAlbumGradingService grader,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings, CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured) return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled) return GradingDisabledProblem();

        var albumId = await ResolveAlbumIdAsync(artist, album, db, ct);
        if (albumId is null)
            return Results.NotFound(new { message = "Album is not linked to a provider album yet." });

        var result = await grader.GradeAlbumAsync(albumId.Value, force: true, ct);
        if (result.Outcome == GradeOutcome.NotConfigured) return NotConfiguredProblem();
        if (result.Outcome == GradeOutcome.Failed)
            return Results.Problem(title: "Grading failed", detail: result.Error, statusCode: 502,
                extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });

        var g = result.Grade;
        return Results.Ok(new
        {
            canonicalAlbumId = albumId,
            outcome = result.Outcome.ToString(),
            score = g?.Score,
            verdict = g?.Verdict.ToString(),
            summary = g?.Summary,
            issues = ParseIssues(g?.IssuesJson),
            gradedAtUtc = g?.GradedAtUtc,
        });
    }

    private static async Task<IResult> GradeAll(
        MusicHoarderDbContext db, AlbumGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings, CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured) return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled) return GradingDisabledProblem();

        var ids = await db.CanonicalAlbums
            .Where(a => a.Status == CanonicalAlbumStatus.Fetched)
            .Select(a => a.Id)
            .ToListAsync(ct);

        channel.EnqueueRange(ids, force: false);
        return Results.Ok(new { enqueued = ids.Count });
    }

    private static async Task<IResult> GradeOutdated(
        MusicHoarderDbContext db, AlbumGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings, CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured) return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled) return GradingDisabledProblem();

        var currentModel = options.CurrentValue.Model;

        // Albums whose latest grade is outdated (prompt version or model changed). force:false is fine —
        // the grader's own staleness check still re-grades on a version/model mismatch.
        var ids = await LatestGrades(db)
            .Where(g => g.PromptVersion != AlbumGradingPrompt.Version || g.Model != currentModel)
            .Join(
                db.CanonicalAlbums.Where(a => a.Status == CanonicalAlbumStatus.Fetched),
                g => g.CanonicalAlbumId, a => a.Id, (g, a) => a.Id)
            .ToListAsync(ct);

        channel.EnqueueRange(ids, force: false);
        return Results.Ok(new { enqueued = ids.Count });
    }

    private static IResult GetProgress(
        AlbumGradingProgressTracker tracker, IOptionsMonitor<QualityGradingOptions> options)
    {
        var configured = options.CurrentValue.IsConfigured;
        var err = tracker.GetLastError();
        object? lastError = err is not null ? new { code = err.Code, message = err.Message, atUtc = err.AtUtc } : null;

        var state = tracker.GetCurrent();
        if (state is null)
            return Results.Ok(new { active = false, aiGradingConfigured = configured, lastError });

        return Results.Ok(new
        {
            active = !state.IsComplete,
            aiGradingConfigured = configured,
            lastError,
            runId = state.RunId,
            total = state.Total,
            processed = state.Processed,
            graded = state.Graded,
            skipped = state.Skipped,
            failed = state.Failed,
            isComplete = state.IsComplete,
            startedAt = state.StartedAt,
            completedAt = state.CompletedAt,
        });
    }

    private static async Task<IResult> ExportAlbum(
        string artist, string album, MusicHoarderDbContext db, IAlbumGradingService grader, CancellationToken ct)
    {
        var albumId = await ResolveAlbumIdAsync(artist, album, db, ct);
        if (albumId is null)
            return Results.NotFound(new { message = "Album is not linked to a provider album yet." });

        var dossier = await grader.BuildDossierAsync(albumId.Value, ct);
        if (dossier is null)
            return Results.NotFound(new { message = "Album is not linked to a provider album yet." });

        var grade = await db.CanonicalAlbumQualityGrades
            .Where(g => g.CanonicalAlbumId == albumId)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            dossier,
            grade = grade is null ? null : new
            {
                score = grade.Score,
                verdict = grade.Verdict.ToString(),
                summary = grade.Summary,
                issues = ParseIssues(grade.IssuesJson),
                model = grade.Model,
                promptVersion = grade.PromptVersion,
                gradedAtUtc = grade.GradedAtUtc,
            },
        });
    }

    /// <summary>Resolves a fetched canonical album id from the frontend's artist+album (normalized identity).</summary>
    private static async Task<int?> ResolveAlbumIdAsync(string artist, string album, MusicHoarderDbContext db, CancellationToken ct)
    {
        var artistKey = TitleNormalizer.NormalizeForSearch(artist);
        var albumKey = TitleNormalizer.NormalizeForSearch(album);
        if (artistKey.Length == 0 || albumKey.Length == 0)
            return null;

        return await db.CanonicalAlbums
            .Where(a => a.ArtistKey == artistKey && a.AlbumKey == albumKey && a.Status == CanonicalAlbumStatus.Fetched)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static List<GradingIssue> ParseIssues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<GradingIssue>>(json, Json) ?? []; }
        catch { return []; }
    }

    private static IResult NotConfiguredProblem() =>
        Results.Problem(title: "AI grading not configured",
            detail: "Set QualityGrading:ApiKey (and optionally BaseUrl/Model) to enable grading.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult GradingDisabledProblem() =>
        Results.Problem(title: "AI grading disabled",
            detail: "AI quality grading is turned off in Settings. Enable it to grade albums.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
