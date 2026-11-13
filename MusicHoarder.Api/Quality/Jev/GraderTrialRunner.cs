using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Quality.Jev;

/// <summary>What one pass of the trial did.</summary>
public sealed record TrialPassResult(int Graded, int Failed, int Stale, int Backfilled)
{
    public static readonly TrialPassResult None = new(0, 0, 0, 0);
    public int Touched => Graded + Failed + Stale;
    public TrialPassResult Add(TrialPassResult o) => new(Graded + o.Graded, Failed + o.Failed, Stale + o.Stale, Backfilled + o.Backfilled);
}

/// <summary>
/// Picks what the grader trial grades next and grades it: subjects whose current authoritative grade
/// has no answer yet from a decision model on the same input. It pairs with the grade the incumbent
/// already wrote, so the trial costs no extra chat calls, and when that grade predates the trial it
/// copies it in as the incumbent's answer (<see cref="GraderRunSource.Backfill"/>, no cost known).
/// </summary>
public sealed class GraderTrialRunner(
    IServiceScopeFactory scopeFactory,
    IJevGradingService jev,
    IOwnerLookupService ownerLookup,
    IOptionsMonitor<JevGradingOptions> options,
    ILogger<GraderTrialRunner> logger)
{
    /// <summary>
    /// Failed decision calls per subject and input before the sweep stops asking (each retry waits
    /// out the back-off). A transient failure gets another chance; a dossier the model rejects every
    /// time does not keep writing failure rows.
    /// </summary>
    public const int MaxDecisionAttempts = 3;

    private readonly RequestRateGate _rateGate = new();

    // One pass at a time: the background sweep and the admin "run now" would otherwise pick the same
    // unanswered subjects and pay for each twice.
    private readonly SemaphoreSlim _passGate = new(1, 1);

    // Subjects whose decision call failed, or whose dossier moved on from their grade. In memory only.
    private readonly FailureBackoffTracker _backoff = new();

    private sealed record Candidate(GraderSubjectKind Kind, int SubjectId, string Fingerprint);

    /// <summary>Grades up to <paramref name="limit"/> subjects of <paramref name="kind"/> per configured decision model.</summary>
    public async Task<TrialPassResult> RunAsync(GraderSubjectKind kind, int limit, GraderRunSource source, CancellationToken ct)
    {
        await _passGate.WaitAsync(ct);
        try { return await RunCoreAsync(kind, limit, source, ct); }
        finally { _passGate.Release(); }
    }

    private async Task<TrialPassResult> RunCoreAsync(GraderSubjectKind kind, int limit, GraderRunSource source, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var total = TrialPassResult.None;
        _backoff.Prune(DateTime.UtcNow);

        foreach (var model in opts.ModelList)
        {
            var candidates = kind == GraderSubjectKind.Song
                ? await SongCandidatesAsync(model, opts, limit, ct)
                : await AlbumCandidatesAsync(model, opts, limit, ct);
            if (candidates.Count == 0) continue;

            total = total.Add(await GradeAsync(candidates, model, source, opts, ct));
        }

        if (total.Touched > 0)
            logger.LogInformation(
                "Grader trial pass ({Kind}): {Graded} graded, {Failed} failed, {Stale} stale, {Backfilled} incumbent grades copied in",
                kind, total.Graded, total.Failed, total.Stale, total.Backfilled);
        return total;
    }

    /// <summary>
    /// Re-grades subjects the primary decision model has answered once, on the same input, to measure
    /// how often a verdict flips. With <paramref name="includeIncumbent"/> the chat grader is asked
    /// again too — that costs real chat calls, which is why it is opt-in.
    /// </summary>
    public async Task<TrialPassResult> RunStabilityAsync(GraderSubjectKind kind, int limit, bool includeIncumbent, CancellationToken ct)
    {
        await _passGate.WaitAsync(ct);
        try { return await RunStabilityCoreAsync(kind, limit, includeIncumbent, ct); }
        finally { _passGate.Release(); }
    }

    private async Task<TrialPassResult> RunStabilityCoreAsync(GraderSubjectKind kind, int limit, bool includeIncumbent, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (opts.PrimaryModel is not { } model) return TrialPassResult.None;

        List<Candidate> candidates;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var version = QuestionVersion(kind);
            var answered = db.GraderComparisonRuns.IgnoreQueryFilters()
                .Where(r => r.SubjectKind == kind && r.Arm == GraderArm.Decision && r.Model == model
                    && r.Version == version && r.ErrorCode == null);

            candidates = await answered
                .Where(r => r.Source != GraderRunSource.Stability
                    && !answered.Any(o => o.SubjectId == r.SubjectId && o.InputFingerprint == r.InputFingerprint
                        && o.Source == GraderRunSource.Stability))
                .OrderByDescending(r => r.CreatedAtUtc)
                .Select(r => new Candidate(kind, r.SubjectId, r.InputFingerprint))
                .Take(limit * 2)
                .ToListAsync(ct);
            candidates = candidates.DistinctBy(c => (c.SubjectId, c.Fingerprint)).Take(limit).ToList();
        }

        var result = await GradeAsync(candidates, model, GraderRunSource.Stability, opts, ct);
        if (!includeIncumbent) return result;

        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await _rateGate.WaitAsync(opts.RequestsPerSecond, ct);
            var r = await jev.RegradeWithIncumbentAsync(c.Kind, c.SubjectId, c.Fingerprint, ct);
            result = result.Add(Count(r));
        }
        return result;
    }

    private async Task<TrialPassResult> GradeAsync(
        IReadOnlyList<Candidate> candidates, string model, GraderRunSource source, JevGradingOptions opts, CancellationToken ct)
    {
        var backfilled = source == GraderRunSource.Stability ? 0 : await BackfillIncumbentAsync(candidates, ct);

        var results = new System.Collections.Concurrent.ConcurrentBag<TrialPassResult>();
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opts.Concurrency), CancellationToken = ct },
            async (c, token) =>
            {
                await _rateGate.WaitAsync(opts.RequestsPerSecond, token);
                TrialPassResult counted;
                try
                {
                    counted = Count(c.Kind == GraderSubjectKind.Song
                        ? await jev.GradeSongAsync(c.SubjectId, model, c.Fingerprint, source, token)
                        : await jev.GradeAlbumAsync(c.SubjectId, model, c.Fingerprint, source, token));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Grader trial failed on {Kind} {SubjectId}", c.Kind, c.SubjectId);
                    counted = new TrialPassResult(0, 1, 0, 0);
                }

                if (counted.Failed > 0 || counted.Stale > 0)
                    _backoff.MarkFailed(Key(c), TimeSpan.FromSeconds(opts.FailureBackoffSeconds));
                results.Add(counted);
            });

        return results.Aggregate(new TrialPassResult(0, 0, 0, backfilled), (sum, r) => sum.Add(r));
    }

    private static TrialPassResult Count(TrialRunResult r) => r.Status switch
    {
        TrialRunStatus.Recorded when r.Run?.ErrorCode is null => new(1, 0, 0, 0),
        TrialRunStatus.Recorded => new(0, 1, 0, 0),
        TrialRunStatus.Stale or TrialRunStatus.NotFound => new(0, 0, 1, 0),
        _ => TrialPassResult.None,
    };

    /// <summary>Song candidates: current grades with no answer yet from <paramref name="model"/> on their input.</summary>
    private async Task<List<Candidate>> SongCandidatesAsync(string model, JevGradingOptions opts, int limit, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var percent = SamplePercent(opts.ShadowSampleRate);
        if (percent == 0) return [];

        var runs = db.GraderComparisonRuns.IgnoreQueryFilters();
        var rows = await db.SongQualityGrades.IgnoreQueryFilters().LatestPerSong()
            .Where(g => g.OwnerUserId != WellKnownUsers.DemoId
                && g.InputFingerprint != null
                && g.PromptVersion == QualityGradingPrompt.Version
                && g.SongId % 100 < percent
                && !runs.Any(r => r.SubjectKind == GraderSubjectKind.Song && r.SubjectId == g.SongId
                    && r.InputFingerprint == g.InputFingerprint && r.Arm == GraderArm.Decision
                    && r.Model == model && r.Version == JevQuestions.SongVersion && r.ErrorCode == null)
                && runs.Count(r => r.SubjectKind == GraderSubjectKind.Song && r.SubjectId == g.SongId
                    && r.InputFingerprint == g.InputFingerprint && r.Arm == GraderArm.Decision
                    && r.Model == model && r.Version == JevQuestions.SongVersion && r.ErrorCode != null) < MaxDecisionAttempts)
            .OrderByDescending(g => g.GradedAtUtc)
            // Subjects in back-off are dropped below; take enough rows that they can never fill the page.
            .Take(limit + _backoff.Count)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        return rows
            .Where(g => !g.IsBlank())
            .Select(g => new Candidate(GraderSubjectKind.Song, g.SongId, g.InputFingerprint!))
            .Where(c => !_backoff.IsBackingOff(Key(c), now))
            .Take(limit)
            .ToList();
    }

    private async Task<List<Candidate>> AlbumCandidatesAsync(string model, JevGradingOptions opts, int limit, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var percent = SamplePercent(opts.ShadowSampleRate);
        if (percent == 0) return [];

        // The album grader judges against one owner's library (IOwnerLookupService), so the trial does too.
        var owner = ownerLookup.OwnerUserId;
        var runs = db.GraderComparisonRuns.IgnoreQueryFilters();
        var rows = await db.CanonicalAlbumQualityGrades.IgnoreQueryFilters()
            .Where(g => g.OwnerUserId == owner)
            .LatestPerAlbum()
            .Where(g => g.InputFingerprint != null
                && g.PromptVersion == AlbumGradingPrompt.Version
                && g.CanonicalAlbumId % 100 < percent
                && !runs.Any(r => r.SubjectKind == GraderSubjectKind.Album && r.SubjectId == g.CanonicalAlbumId
                    && r.InputFingerprint == g.InputFingerprint && r.Arm == GraderArm.Decision
                    && r.Model == model && r.Version == JevQuestions.AlbumVersion && r.ErrorCode == null)
                && runs.Count(r => r.SubjectKind == GraderSubjectKind.Album && r.SubjectId == g.CanonicalAlbumId
                    && r.InputFingerprint == g.InputFingerprint && r.Arm == GraderArm.Decision
                    && r.Model == model && r.Version == JevQuestions.AlbumVersion && r.ErrorCode != null) < MaxDecisionAttempts)
            .OrderByDescending(g => g.GradedAtUtc)
            // Subjects in back-off are dropped below; take enough rows that they can never fill the page.
            .Take(limit + _backoff.Count)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        return rows
            .Where(g => !g.IsBlank())
            .Select(g => new Candidate(GraderSubjectKind.Album, g.CanonicalAlbumId, g.InputFingerprint!))
            .Where(c => !_backoff.IsBackingOff(Key(c), now))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Copies in the incumbent's answer for candidates whose grade was written before the trial
    /// recorded grades, so every decision answer has an incumbent answer on the same input.
    /// </summary>
    private async Task<int> BackfillIncumbentAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var kind = candidates[0].Kind;
        var ids = candidates.Select(c => c.SubjectId).ToList();
        var fingerprints = candidates.Select(c => c.Fingerprint).ToList();

        var have = (await db.GraderComparisonRuns.IgnoreQueryFilters()
                .Where(r => r.SubjectKind == kind && r.Arm == GraderArm.Incumbent
                    && ids.Contains(r.SubjectId) && fingerprints.Contains(r.InputFingerprint))
                .Select(r => new { r.SubjectId, r.InputFingerprint })
                .ToListAsync(ct))
            .Select(r => (r.SubjectId, r.InputFingerprint))
            .ToHashSet();

        var missing = candidates.Where(c => !have.Contains((c.SubjectId, c.Fingerprint))).ToList();
        if (missing.Count == 0) return 0;

        var missingIds = missing.Select(c => c.SubjectId).ToList();
        var wanted = missing.Select(c => (c.SubjectId, c.Fingerprint)).ToHashSet();
        var added = 0;
        if (kind == GraderSubjectKind.Song)
        {
            var grades = await db.SongQualityGrades.IgnoreQueryFilters().LatestPerSong()
                .Where(g => missingIds.Contains(g.SongId)).ToListAsync(ct);
            foreach (var g in grades.Where(g => wanted.Contains((g.SongId, g.InputFingerprint!))))
            {
                db.GraderComparisonRuns.Add(GraderTrialRows.FromSongGrade(g));
                added++;
            }
        }
        else
        {
            var owner = ownerLookup.OwnerUserId;
            var grades = await db.CanonicalAlbumQualityGrades.IgnoreQueryFilters()
                .Where(g => g.OwnerUserId == owner).LatestPerAlbum()
                .Where(g => missingIds.Contains(g.CanonicalAlbumId)).ToListAsync(ct);
            foreach (var g in grades.Where(g => wanted.Contains((g.CanonicalAlbumId, g.InputFingerprint!))))
            {
                db.GraderComparisonRuns.Add(GraderTrialRows.FromAlbumGrade(g));
                added++;
            }
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    internal static int QuestionVersion(GraderSubjectKind kind) =>
        kind == GraderSubjectKind.Song ? JevQuestions.SongVersion : JevQuestions.AlbumVersion;

    /// <summary>The sample is "subject id mod 100 below the rate", so the same subjects stay in it as the library grows.</summary>
    internal static int SamplePercent(double rate) => (int)Math.Round(Math.Clamp(rate, 0, 1) * 100);

    // Song and album ids share one tracker; albums are offset into the negative range.
    private static int Key(Candidate c) => c.Kind == GraderSubjectKind.Song ? c.SubjectId : -c.SubjectId - 1;
}

/// <summary>Runs <see cref="GraderTrialRunner"/> in the background while the trial is on and quality grading is enabled.</summary>
public sealed class JevShadowGradingBackgroundService(
    GraderTrialRunner runner,
    IDecisionClient decisionClient,
    Settings.IRuntimeSettingsService runtimeSettings,
    IOptionsMonitor<JevGradingOptions> options,
    ILogger<JevShadowGradingBackgroundService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        IdleBackoffSweepLoop.RunAsync(
            // Let the incumbent grader's own sweep start first: the trial only pairs with its grades.
            initialDelay: TimeSpan.FromSeconds(60),
            baseIdleSeconds: () => options.CurrentValue.IdleDelaySeconds,
            maxIdleSeconds: 900,
            sweep: async token =>
            {
                var opts = options.CurrentValue;
                if (!decisionClient.IsConfigured) return false;
                if (!(await runtimeSettings.GetAsync(token).ConfigureAwait(false)).QualityGradingEnabled) return false;

                var result = await runner.RunAsync(GraderSubjectKind.Song, opts.BatchSize, GraderRunSource.Shadow, token);
                if (opts.GradeAlbums)
                    result = result.Add(await runner.RunAsync(GraderSubjectKind.Album, opts.BatchSize, GraderRunSource.Shadow, token));
                return result.Graded > 0;
            },
            onSweepFailed: ex => logger.LogWarning(ex, "Grader trial sweep failed"),
            ct: stoppingToken);
}
