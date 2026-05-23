using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// AI library-quality grading + the raw data export used to debug bad algorithm decisions.
/// All reads go through the request-scoped (owner-filtered) DbContext, so a user only ever sees
/// their own songs and grades.
/// </summary>
public static class QualityEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly EnrichmentStatus[] GradeableStatuses =
        [EnrichmentStatus.Matched, EnrichmentStatus.NeedsReview];

    public static IEndpointRouteBuilder MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/quality/overview", GetOverview)
            .WithName("GetQualityOverview")
            .WithSummary("Library-wide AI quality rollup: verdict counts, average score, top issues, worst offenders, per-directory breakdown.")
            .WithTags("Quality");

        app.MapGet("/api/quality/directories", GetDirectories)
            .WithName("GetQualityDirectories")
            .WithSummary("Per-directory AI quality rollups.")
            .WithTags("Quality");

        app.MapGet("/api/quality/songs/{id:int}", GetSongGrade)
            .WithName("GetSongQualityGrade")
            .WithSummary("Latest AI quality grade for one song, plus its grade history count.")
            .WithTags("Quality");

        app.MapPost("/api/quality/songs/{id:int}/grade", GradeSong)
            .WithName("GradeSongQuality")
            .WithSummary("Grade one song now (forces a fresh LLM call even if unchanged).")
            .WithTags("Quality");

        app.MapPost("/api/quality/grade-all", GradeAll)
            .WithName("GradeAllQuality")
            .WithSummary("Enqueue every gradeable song for AI grading (skips unchanged).")
            .WithTags("Quality");

        app.MapPost("/api/quality/grade-directory", GradeDirectory)
            .WithName("GradeDirectoryQuality")
            .WithSummary("Enqueue every gradeable song under a source directory for AI grading.")
            .WithTags("Quality");

        app.MapGet("/api/quality/progress", GetProgress)
            .WithName("GetQualityProgress")
            .WithSummary("Current AI grading run progress.")
            .WithTags("Quality");

        app.MapGet("/api/quality/export/songs/{id:int}", ExportSong)
            .WithName("ExportSongQuality")
            .WithSummary("Download the full dossier + grade for one song (for feeding to an agent).")
            .WithTags("Quality");

        app.MapGet("/api/quality/export/directory", ExportDirectory)
            .WithName("ExportDirectoryQuality")
            .WithSummary("Download dossiers + grades for every song under a source directory.")
            .WithTags("Quality");

        app.MapGet("/api/quality/export/library", ExportLibrary)
            .WithName("ExportLibraryQuality")
            .WithSummary("Download dossiers + grades for the whole library.")
            .WithTags("Quality");

        return app;
    }

    /// <summary>Latest grade per song for the current owner (correlated-subquery "no newer grade exists").</summary>
    private static IQueryable<SongQualityGrade> LatestGrades(MusicHoarderDbContext db) =>
        db.SongQualityGrades.Where(g =>
            !db.SongQualityGrades.Any(g2 => g2.SongId == g.SongId && g2.GradedAtUtc > g.GradedAtUtc));

    private record GradeRowDto(
        int SongId,
        string SourcePath,
        string FileName,
        string? Artist,
        string? Title,
        string? Album,
        int Score,
        SongQualityVerdict Verdict,
        string? Summary,
        string? IssuesJson,
        string? EnrichmentStatusAtGrade,
        string? DestinationPathPreview,
        DateTime GradedAtUtc);

    private static async Task<List<GradeRowDto>> LoadGradeRowsAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        return await LatestGrades(db)
            .Join(db.Songs.Where(s => s.DeletedAtUtc == null),
                g => g.SongId, s => s.Id,
                (g, s) => new GradeRowDto(
                    s.Id, s.SourcePath, s.FileName, s.Artist, s.Title, s.Album,
                    g.Score, g.Verdict, g.Summary, g.IssuesJson,
                    g.EnrichmentStatusAtGrade, g.DestinationPathPreview, g.GradedAtUtc))
            .ToListAsync(ct);
    }

    private static string DirectoryOf(string sourcePath)
        => Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "/";

    private static object ToWorstOffender(GradeRowDto r) => new
    {
        songId = r.SongId,
        fileName = r.FileName,
        sourcePath = r.SourcePath,
        artist = r.Artist,
        title = r.Title,
        album = r.Album,
        score = r.Score,
        verdict = r.Verdict.ToString(),
        summary = r.Summary,
        issues = ParseIssues(r.IssuesJson),
        enrichmentStatusAtGrade = r.EnrichmentStatusAtGrade,
        destinationPathPreview = r.DestinationPathPreview,
        gradedAtUtc = r.GradedAtUtc,
    };

    private static object AggregateToDto(QualityAggregate agg) => new
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
    };

    private static List<GradingIssue> ParseIssues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<GradingIssue>>(json, Json) ?? []; }
        catch { return []; }
    }

    private static async Task<IResult> GetOverview(MusicHoarderDbContext db, CancellationToken ct)
    {
        var rows = await LoadGradeRowsAsync(db, ct);

        var gradeableTotal = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate && GradeableStatuses.Contains(s.EnrichmentStatus))
            .CountAsync(ct);

        var agg = QualityRollup.Aggregate(rows.Select(r => new QualityRollup.GradeRow(r.Verdict, r.Score, r.IssuesJson)));

        var worst = rows
            .OrderBy(r => (int)r.Verdict)
            .ThenBy(r => r.Score)
            .ThenByDescending(r => r.GradedAtUtc)
            .Take(50)
            .Select(ToWorstOffender)
            .ToList();

        var directories = BuildDirectoryRollups(rows).Take(200).ToList();

        return Results.Ok(new
        {
            gradeableTotal,
            coverage = gradeableTotal == 0 ? 0d : Math.Round((double)rows.Count / gradeableTotal, 3),
            library = AggregateToDto(agg),
            worstOffenders = worst,
            directories,
        });
    }

    private static IEnumerable<object> BuildDirectoryRollups(List<GradeRowDto> rows)
    {
        return rows
            .GroupBy(r => DirectoryOf(r.SourcePath))
            .Select(grp =>
            {
                var agg = QualityRollup.Aggregate(
                    grp.Select(r => new QualityRollup.GradeRow(r.Verdict, r.Score, r.IssuesJson)));
                var worstInDir = (SongQualityVerdict)grp.Min(r => (int)r.Verdict);
                return (
                    directory: grp.Key,
                    agg,
                    worstVerdict: worstInDir,
                    wrongCount: agg.Verdicts.Wrong);
            })
            // Surface the most problematic directories first: most wrong tracks, then lowest average.
            .OrderByDescending(d => d.wrongCount)
            .ThenBy(d => d.agg.AverageScore ?? 1000d)
            .Select(d => (object)new
            {
                directory = d.directory,
                rollup = AggregateToDto(d.agg),
                worstVerdict = d.worstVerdict.ToString(),
                wrongCount = d.wrongCount,
            });
    }

    private static async Task<IResult> GetDirectories(MusicHoarderDbContext db, CancellationToken ct)
    {
        var rows = await LoadGradeRowsAsync(db, ct);
        return Results.Ok(new { directories = BuildDirectoryRollups(rows).ToList() });
    }

    private static async Task<IResult> GetSongGrade(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var grade = await db.SongQualityGrades
            .Where(g => g.SongId == id)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (grade is null)
            return Results.Ok(new { songId = id, graded = false });

        var historyCount = await db.SongQualityGrades.CountAsync(g => g.SongId == id, ct);

        return Results.Ok(new
        {
            songId = id,
            graded = true,
            score = grade.Score,
            verdict = grade.Verdict.ToString(),
            summary = grade.Summary,
            issues = ParseIssues(grade.IssuesJson),
            model = grade.Model,
            promptVersion = grade.PromptVersion,
            enrichmentStatusAtGrade = grade.EnrichmentStatusAtGrade,
            destinationPathPreview = grade.DestinationPathPreview,
            durationMs = grade.DurationMs,
            gradedAtUtc = grade.GradedAtUtc,
            historyCount,
        });
    }

    private static async Task<IResult> GradeSong(
        int id, MusicHoarderDbContext db, IQualityGradingService grader,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        // Ownership check via the filtered context — a cross-tenant id 404s here before grading.
        var exists = await db.Songs.AnyAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (!exists)
            return Results.NotFound(new { message = $"Song {id} not found." });

        var result = await grader.GradeSongAsync(id, force: true, ct);

        if (result.Outcome == GradeOutcome.NotConfigured)
            return NotConfiguredProblem();

        if (result.Outcome is GradeOutcome.Failed)
            return Results.Problem(
                title: "Grading failed", detail: result.Error, statusCode: 502,
                extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });

        var g = result.Grade;
        return Results.Ok(new
        {
            songId = id,
            outcome = result.Outcome.ToString(),
            score = g?.Score,
            verdict = g?.Verdict.ToString(),
            summary = g?.Summary,
            issues = ParseIssues(g?.IssuesJson),
            model = g?.Model,
            destinationPathPreview = g?.DestinationPathPreview,
            durationMs = g?.DurationMs,
            gradedAtUtc = g?.GradedAtUtc,
        });
    }

    private static async Task<IResult> GradeAll(
        MusicHoarderDbContext db, QualityGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate && GradeableStatuses.Contains(s.EnrichmentStatus))
            .Select(s => s.Id)
            .ToListAsync(ct);

        channel.EnqueueRange(ids, force: false);
        return Results.Ok(new { enqueued = ids.Count });
    }

    private record GradeDirectoryRequest(string Path);

    private static async Task<IResult> GradeDirectory(
        GradeDirectoryRequest request, MusicHoarderDbContext db, QualityGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(new { message = "path is required." });

        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate
                && GradeableStatuses.Contains(s.EnrichmentStatus)
                && s.SourcePath.StartsWith(request.Path))
            .Select(s => s.Id)
            .ToListAsync(ct);

        channel.EnqueueRange(ids, force: false);
        return Results.Ok(new { enqueued = ids.Count, path = request.Path });
    }

    private static async Task<IResult> GetProgress(
        QualityGradingProgressTracker tracker, IOptionsMonitor<QualityGradingOptions> options,
        IRuntimeSettingsService runtimeSettings, CancellationToken ct)
    {
        var configured = options.CurrentValue.IsConfigured;
        var enabled = (await runtimeSettings.GetAsync(ct)).QualityGradingEnabled;
        var err = tracker.GetLastError();
        // Suppress the error surface entirely while grading is disabled, so turning it off in
        // Settings makes the failure banners disappear.
        object? lastError = enabled && err is not null
            ? new { code = err.Code, message = err.Message, atUtc = err.AtUtc }
            : null;

        var state = tracker.GetCurrent();
        if (state is null)
            return Results.Ok(new { active = false, aiGradingConfigured = configured, aiGradingEnabled = enabled, lastError });

        return Results.Ok(new
        {
            active = !state.IsComplete,
            aiGradingConfigured = configured,
            aiGradingEnabled = enabled,
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

    private static IResult NotConfiguredProblem() =>
        Results.Problem(
            title: "AI grading not configured",
            detail: "Set QualityGrading:ApiKey (and optionally BaseUrl/Model) to enable grading.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult GradingDisabledProblem() =>
        Results.Problem(
            title: "AI grading disabled",
            detail: "AI quality grading is turned off in Settings. Enable it to grade songs.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    // --- Export ---

    private static async Task<object?> BuildExportItemAsync(
        int songId, MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        var song = await db.Songs
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == songId && s.DeletedAtUtc == null, ct);
        if (song is null) return null;

        var changes = await db.SongMetadataChanges.Where(c => c.SongId == songId).ToListAsync(ct);
        var dossier = factory.Build(song, changes);

        var grade = await db.SongQualityGrades
            .Where(g => g.SongId == songId)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        return new { dossier, grade = GradeExport(grade) };
    }

    private static object? GradeExport(SongQualityGrade? g) => g is null ? null : new
    {
        score = g.Score,
        verdict = g.Verdict.ToString(),
        summary = g.Summary,
        issues = ParseIssues(g.IssuesJson),
        model = g.Model,
        promptVersion = g.PromptVersion,
        enrichmentStatusAtGrade = g.EnrichmentStatusAtGrade,
        destinationPathPreview = g.DestinationPathPreview,
        gradedAtUtc = g.GradedAtUtc,
    };

    private static async Task<IResult> ExportSong(
        int id, MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        var item = await BuildExportItemAsync(id, db, factory, ct);
        if (item is null) return Results.NotFound(new { message = $"Song {id} not found." });
        return Results.Ok(item);
    }

    private static async Task<IResult> ExportDirectory(
        string path, MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { message = "path query parameter is required." });

        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null && s.SourcePath.StartsWith(path))
            .OrderBy(s => s.SourcePath)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var items = new List<object>();
        foreach (var sid in ids)
        {
            var item = await BuildExportItemAsync(sid, db, factory, ct);
            if (item is not null) items.Add(item);
        }

        return Results.Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            scope = "directory",
            path,
            count = items.Count,
            items,
        });
    }

    private static async Task<IResult> ExportLibrary(
        MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null)
            .OrderBy(s => s.SourcePath)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var items = new List<object>();
        foreach (var sid in ids)
        {
            var item = await BuildExportItemAsync(sid, db, factory, ct);
            if (item is not null) items.Add(item);
        }

        return Results.Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            scope = "library",
            count = items.Count,
            items,
        });
    }
}
