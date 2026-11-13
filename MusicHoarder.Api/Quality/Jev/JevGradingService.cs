using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality.Jev;

public enum TrialRunStatus
{
    /// <summary>An answer (or a recorded failure) was written to the trial table.</summary>
    Recorded,
    /// <summary>The subject is gone, or its album is not linked.</summary>
    NotFound,
    /// <summary>The subject changed since the grade being paired with, so the answers would not be on the same input.</summary>
    Stale,
    /// <summary>The trial or quality grading is off.</summary>
    NotConfigured,
}

public sealed record TrialRunResult(TrialRunStatus Status, GraderComparisonRun? Run = null);

public interface IJevGradingService
{
    /// <summary>
    /// Grades one song with one decision model and records the answer in the trial table — never in
    /// <c>SongQualityGrades</c>. With <paramref name="expectedFingerprint"/>, does nothing unless the
    /// song's dossier still hashes to it (the grade it is paired with must have seen the same input).
    /// </summary>
    Task<TrialRunResult> GradeSongAsync(int songId, string model, string? expectedFingerprint, GraderRunSource source, CancellationToken ct = default);

    /// <summary>The album counterpart of <see cref="GradeSongAsync"/>.</summary>
    Task<TrialRunResult> GradeAlbumAsync(int canonicalAlbumId, string model, string? expectedFingerprint, GraderRunSource source, CancellationToken ct = default);

    /// <summary>
    /// Asks the chat grader again on the same input, for the stability measure. The answer goes to
    /// the trial table only; the authoritative grade is not touched.
    /// </summary>
    Task<TrialRunResult> RegradeWithIncumbentAsync(GraderSubjectKind kind, int subjectId, string expectedFingerprint, CancellationToken ct = default);
}

public sealed class JevGradingService(
    IServiceScopeFactory scopeFactory,
    IDecisionClient decisionClient,
    IChatCompletionClient chatClient,
    IQualityDossierFactory songDossiers,
    IAlbumGradingService albumGrading,
    IOwnerLookupService ownerLookup,
    IOptionsMonitor<JevGradingOptions> options,
    IOptionsMonitor<QualityGradingOptions> gradingOptions,
    ILogger<JevGradingService> logger) : IJevGradingService
{
    public async Task<TrialRunResult> GradeSongAsync(
        int songId, string model, string? expectedFingerprint, GraderRunSource source, CancellationToken ct = default)
    {
        if (!decisionClient.IsConfigured)
            return new TrialRunResult(TrialRunStatus.NotConfigured);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var loaded = await QualityGradingService.LoadDossierAsync(db, songDossiers, songId, ct);
        if (loaded is null)
            return new TrialRunResult(TrialRunStatus.NotFound);
        var (song, dossier) = loaded.Value;

        var fingerprint = DossierFingerprint.Of(dossier);
        if (expectedFingerprint is not null && expectedFingerprint != fingerprint)
            return new TrialRunResult(TrialRunStatus.Stale);

        var state = JevQuestions.SongState(dossier, options.CurrentValue.MaxStateBytes);
        var run = await DecideAsync(
            GraderSubjectKind.Song, song.Id, song.OwnerUserId, model, JevQuestions.SongVersion, fingerprint,
            state, JevQuestions.SongQuestions, JevQuestions.MapSong, source, ct);

        db.GraderComparisonRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return new TrialRunResult(TrialRunStatus.Recorded, run);
    }

    public async Task<TrialRunResult> GradeAlbumAsync(
        int canonicalAlbumId, string model, string? expectedFingerprint, GraderRunSource source, CancellationToken ct = default)
    {
        if (!decisionClient.IsConfigured)
            return new TrialRunResult(TrialRunStatus.NotConfigured);

        var dossier = await albumGrading.BuildDossierAsync(canonicalAlbumId, ct);
        if (dossier is null)
            return new TrialRunResult(TrialRunStatus.NotFound);

        var fingerprint = DossierFingerprint.Of(dossier);
        if (expectedFingerprint is not null && expectedFingerprint != fingerprint)
            return new TrialRunResult(TrialRunStatus.Stale);

        var run = await DecideAsync(
            GraderSubjectKind.Album, canonicalAlbumId, ownerLookup.OwnerUserId, model, JevQuestions.AlbumVersion, fingerprint,
            JevQuestions.AlbumState(dossier), JevQuestions.AlbumQuestions, JevQuestions.MapAlbum, source, ct);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        db.GraderComparisonRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return new TrialRunResult(TrialRunStatus.Recorded, run);
    }

    public async Task<TrialRunResult> RegradeWithIncumbentAsync(
        GraderSubjectKind kind, int subjectId, string expectedFingerprint, CancellationToken ct = default)
    {
        var opts = gradingOptions.CurrentValue;
        if (!chatClient.IsConfigured || !options.CurrentValue.Enabled)
            return new TrialRunResult(TrialRunStatus.NotConfigured);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        IReadOnlyList<ChatMessage> messages;
        Guid owner;
        int version;
        if (kind == GraderSubjectKind.Song)
        {
            var loaded = await QualityGradingService.LoadDossierAsync(db, songDossiers, subjectId, ct);
            if (loaded is null) return new TrialRunResult(TrialRunStatus.NotFound);
            if (DossierFingerprint.Of(loaded.Value.Dossier) != expectedFingerprint) return new TrialRunResult(TrialRunStatus.Stale);
            messages = QualityGradingPrompt.BuildMessages(loaded.Value.Dossier);
            owner = loaded.Value.Song.OwnerUserId;
            version = QualityGradingPrompt.Version;
        }
        else
        {
            var dossier = await albumGrading.BuildDossierAsync(subjectId, ct);
            if (dossier is null) return new TrialRunResult(TrialRunStatus.NotFound);
            if (DossierFingerprint.Of(dossier) != expectedFingerprint) return new TrialRunResult(TrialRunStatus.Stale);
            messages = AlbumGradingPrompt.BuildMessages(dossier);
            owner = ownerLookup.OwnerUserId;
            version = AlbumGradingPrompt.Version;
        }

        var stats = new GradingCallStats();
        var sw = Stopwatch.StartNew();
        GraderComparisonRun run;
        try
        {
            var (parsed, _) = await GradingCompletion.RequestGradeAsync(chatClient, messages, opts, logger, ct, stats);
            run = GraderTrialRows.Incumbent(kind, subjectId, owner, opts.Model, version, expectedFingerprint,
                parsed, stats, Elapsed(sw), null, null, GraderRunSource.Stability);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            run = GraderTrialRows.Incumbent(kind, subjectId, owner, opts.Model, version, expectedFingerprint,
                null, stats, Elapsed(sw), ErrorCode(ex), ex.Message, GraderRunSource.Stability);
        }

        db.GraderComparisonRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return new TrialRunResult(TrialRunStatus.Recorded, run);
    }

    private async Task<GraderComparisonRun> DecideAsync(
        GraderSubjectKind kind, int subjectId, Guid owner, string model, int version, string fingerprint,
        JevState state, IReadOnlyDictionary<string, DecisionQuestion> questions,
        Func<IReadOnlyDictionary<string, DecisionAnswer>, JevGrade> map,
        GraderRunSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await decisionClient.DecideAsync(new DecisionRequest(model, state.State, questions), ct);
            var grade = map(result.Answers);
            return GraderTrialRows.Decision(kind, subjectId, owner, model, version, fingerprint,
                state, grade, result, Elapsed(sw), result.Attempts, null, null, source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Decision model {Model} failed on {Kind} {SubjectId}", model, kind, subjectId);
            return GraderTrialRows.Decision(kind, subjectId, owner, model, version, fingerprint,
                state, null, null, Elapsed(sw), null, ErrorCode(ex), ex.Message, source);
        }
    }

    private static int Elapsed(Stopwatch sw) => (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);

    /// <summary>The same codes the grading runs record, so the two arms' failures compare.</summary>
    internal static string ErrorCode(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } => "out_of_credits",
        HttpRequestException => "http_error",
        TimeoutException => "timeout",
        JsonException => "bad_response",
        InvalidOperationException => "empty_response",
        _ => "error",
    };
}
