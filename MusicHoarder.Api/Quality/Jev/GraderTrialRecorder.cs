using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>Records the incumbent grader's answers for the grader trial while it runs.</summary>
public interface IGraderTrialRecorder
{
    /// <summary>Writes the row when the trial is on. Never throws for a failed write: the trial only observes.</summary>
    Task RecordAsync(GraderComparisonRun run, CancellationToken ct);
}

/// <summary>
/// Writes trial rows in a DB scope of its own, after the grade is saved, so a failed trial write
/// can neither fail nor roll back the grade it describes.
/// </summary>
public sealed class GraderTrialRecorder(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<JevGradingOptions> options,
    ILogger<GraderTrialRecorder> logger) : IGraderTrialRecorder
{
    public async Task RecordAsync(GraderComparisonRun run, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            db.GraderComparisonRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the grader-trial row for {Kind} {SubjectId}", run.SubjectKind, run.SubjectId);
        }
    }
}
