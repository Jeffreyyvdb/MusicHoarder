using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public class QualityGradingService(
    IServiceScopeFactory scopeFactory,
    IChatCompletionClient chatClient,
    IQualityDossierFactory dossierFactory,
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<QualityGradingService> logger) : IQualityGradingService
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public async Task<GradeSongResult> GradeSongAsync(int songId, bool force = false, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var song = await db.Songs
            .IgnoreQueryFilters()
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == songId && s.DeletedAtUtc == null, ct);

        if (song is null)
            return new GradeSongResult(GradeOutcome.NotFound, null);

        var changes = await db.SongMetadataChanges
            .IgnoreQueryFilters()
            .Where(c => c.SongId == songId)
            .ToListAsync(ct);

        var dossier = dossierFactory.Build(song, changes);
        var fingerprint = Fingerprint(dossier);

        var latest = await db.SongQualityGrades
            .IgnoreQueryFilters()
            .Where(g => g.SongId == songId)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (!force
            && latest is not null
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
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await chatClient.CompleteAsync(
                new ChatCompletionRequest(
                    QualityGradingPrompt.BuildMessages(dossier),
                    opts.Temperature,
                    opts.MaxOutputTokens),
                ct);
            rawContent = result.Content;
            parsed = QualityGradingPrompt.Parse(rawContent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quality grading failed for song {SongId}", songId);
            return new GradeSongResult(GradeOutcome.Failed, latest, ex.Message, ClassifyError(ex));
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
            RawResponseJson = rawContent.Length > 8192 ? rawContent[..8192] : rawContent,
            GradedAtUtc = DateTime.UtcNow,
        };

        db.SongQualityGrades.Add(grade);
        await db.SaveChangesAsync(ct);

        return new GradeSongResult(GradeOutcome.Graded, grade);
    }

    /// <summary>Maps a grading failure to a machine-readable code the UI can act on (e.g. show an "out of credits" banner).</summary>
    private static string ClassifyError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } => "out_of_credits",
        HttpRequestException => "http_error",
        InvalidOperationException => "empty_response",
        _ => "error",
    };

    private static string Fingerprint(SongGradingDossier dossier)
    {
        var json = JsonSerializer.Serialize(dossier, CompactJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}
