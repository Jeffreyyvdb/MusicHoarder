using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality.Jev;

namespace MusicHoarder.Api.Quality;

public class QualityGradingService(
    IServiceScopeFactory scopeFactory,
    IChatCompletionClient chatClient,
    IQualityDossierFactory dossierFactory,
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<QualityGradingService> logger,
    IOptionsMonitor<JevGradingOptions>? trialOptions = null) : IQualityGradingService
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public async Task<GradeSongResult> GradeSongAsync(int songId, bool force = false, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var loaded = await LoadDossierAsync(db, dossierFactory, songId, ct);
        if (loaded is null)
            return new GradeSongResult(GradeOutcome.NotFound, null);
        var (song, dossier) = loaded.Value;

        var fingerprint = DossierFingerprint.Of(dossier);

        var latest = await db.SongQualityGrades
            .IgnoreQueryFilters()
            .Where(g => g.SongId == songId)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        // A blank grade (an empty reply stored as "ungradeable") is never reused: it judged nothing.
        if (!force
            && latest is not null
            && !latest.IsBlank()
            && latest.InputFingerprint == fingerprint
            && latest.Model == opts.Model
            && latest.PromptVersion == QualityGradingPrompt.Version)
        {
            return new GradeSongResult(GradeOutcome.Skipped, latest);
        }

        if (!chatClient.IsConfigured)
            return new GradeSongResult(GradeOutcome.NotConfigured, latest);

        GradingResult parsed;
        string rawContent;
        var stats = new GradingCallStats();
        var sw = Stopwatch.StartNew();
        try
        {
            (parsed, rawContent) = await GradingCompletion.RequestGradeAsync(
                chatClient, QualityGradingPrompt.BuildMessages(dossier), opts, logger, ct, stats);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quality grading failed for song {SongId}", songId);
            var code = ClassifyError(ex);
            await RecordTrialRunAsync(db, GraderTrialRows.Incumbent(
                GraderSubjectKind.Song, song.Id, song.OwnerUserId, opts.Model, QualityGradingPrompt.Version, fingerprint,
                null, stats, (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds), code, ex.Message), ct);
            return new GradeSongResult(GradeOutcome.Failed, latest, ex.Message, code);
        }
        sw.Stop();

        var grade = new SongQualityGrade
        {
            SongId = song.Id,
            OwnerUserId = song.OwnerUserId,
            Score = parsed.Score,
            Verdict = parsed.Verdict,
            Summary = parsed.Summary,
            IssuesJson = parsed.Issues.Count > 0 ? JsonSerializer.Serialize(parsed.Issues, CompactJson) : null,
            Model = opts.Model,
            PromptVersion = QualityGradingPrompt.Version,
            InputFingerprint = fingerprint,
            EnrichmentStatusAtGrade = song.EnrichmentStatus.ToString(),
            DestinationPathPreview = dossier.DestinationPathPreview,
            DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
            RawResponseJson = rawContent.Length > BlankGrade.MaxStoredResponseChars
                ? rawContent[..BlankGrade.MaxStoredResponseChars]
                : rawContent,
            GradedAtUtc = DateTime.UtcNow,
        };

        db.SongQualityGrades.Add(grade);
        await db.SaveChangesAsync(ct);

        await RecordTrialRunAsync(db, GraderTrialRows.Incumbent(
            GraderSubjectKind.Song, song.Id, song.OwnerUserId, opts.Model, QualityGradingPrompt.Version, fingerprint,
            parsed, stats, grade.DurationMs, null, null), ct);

        return new GradeSongResult(GradeOutcome.Graded, grade);
    }

    /// <summary>
    /// Loads a live song with what its dossier needs and builds the dossier — the one way to build
    /// it, so the grader trial hashes exactly what the grade hashed. Null when the song is gone.
    /// </summary>
    internal static async Task<(SongMetadata Song, SongGradingDossier Dossier)?> LoadDossierAsync(
        MusicHoarderDbContext db, IQualityDossierFactory dossierFactory, int songId, CancellationToken ct)
    {
        var song = await db.Songs
            .IgnoreQueryFilters()
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == songId && s.DeletedAtUtc == null, ct);
        if (song is null)
            return null;

        var changes = await db.SongMetadataChanges
            .IgnoreQueryFilters()
            .Where(c => c.SongId == songId)
            .ToListAsync(ct);

        return (song, dossierFactory.Build(song, changes));
    }

    /// <summary>
    /// Records the incumbent's answer for the grader trial while it runs. Written after the grade
    /// and never allowed to fail it: the trial only observes.
    /// </summary>
    private async Task RecordTrialRunAsync(MusicHoarderDbContext db, GraderComparisonRun run, CancellationToken ct)
    {
        if (trialOptions?.CurrentValue.Enabled != true) return;
        try
        {
            db.GraderComparisonRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            db.Entry(run).State = EntityState.Detached;
            logger.LogWarning(ex, "Could not record the grader-trial row for song {SongId}", run.SubjectId);
        }
    }

    /// <summary>Maps a grading failure to a machine-readable code the UI can act on (e.g. show an "out of credits" banner).</summary>
    private static string ClassifyError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } => "out_of_credits",
        HttpRequestException => "http_error",
        JsonException => "bad_response",
        InvalidOperationException => "empty_response",
        _ => "error",
    };
}
