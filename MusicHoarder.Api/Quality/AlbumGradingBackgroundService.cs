using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.RateLimiting;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Consumes <see cref="AlbumGradingChannel"/> with bounded concurrency + a request-rate gate shared by
/// its workers, and (when enabled) periodically sweeps for reconciled-but-ungraded/stale albums and
/// enqueues them. Mirrors <see cref="QualityGradingBackgroundService"/> and reads the same LLM
/// settings, but holds its own rate gate, so the two graders' budgets add up rather than share.
/// </summary>
public class AlbumGradingBackgroundService(
    IServiceScopeFactory scopeFactory,
    AlbumGradingChannel channel,
    AlbumGradingProgressTracker progressTracker,
    IAlbumGradingService gradingService,
    IRuntimeSettingsService runtimeSettings,
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<AlbumGradingBackgroundService> logger) : BackgroundService
{
    private readonly RequestRateGate _rateGate = new();
    private int _warnedNotConfigured;

    // Albums that just failed to grade. A failure persists no grade row, so without this backoff the
    // auto-sweep re-enqueues them every sweep — flooding logs and burning API credits on a reply that
    // keeps failing (e.g. an OpenRouter 403). Mirrors the song sweep.
    private readonly FailureBackoffTracker _failureBackoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;
        logger.LogInformation(
            "Album grading service started. Configured={Configured} Auto={Auto} Concurrency={Concurrency}",
            opts.IsConfigured, opts.AutoGradeAlbums, opts.Concurrency);

        var workers = Enumerable.Range(0, Math.Max(1, opts.Concurrency))
            .Select(i => RunWorkerAsync(i, stoppingToken))
            .ToArray();

        await Task.WhenAll([RunAutoSweepLoopAsync(stoppingToken), .. workers]);
    }

    private Task RunAutoSweepLoopAsync(CancellationToken ct) =>
        IdleBackoffSweepLoop.RunAsync(
            // Give the canonical-album fetch sweep a head start so there are albums to grade.
            initialDelay: TimeSpan.FromSeconds(20),
            baseIdleSeconds: () => options.CurrentValue.IdleDelaySeconds,
            maxIdleSeconds: 300,
            sweep: async token =>
            {
                var opts = options.CurrentValue;
                var enabled = (await runtimeSettings.GetAsync(token).ConfigureAwait(false)).QualityGradingEnabled;
                return enabled && opts.IsConfigured && opts.AutoGradeAlbums
                    && await EnqueueUngradedAsync(opts, token) > 0;
            },
            onSweepFailed: ex => logger.LogWarning(ex, "Album auto-grade sweep failed"),
            ct);

    /// <summary>Finds fetched albums whose latest grade is missing or stale and enqueues them. Returns the count enqueued.</summary>
    internal async Task<int> EnqueueUngradedAsync(QualityGradingOptions opts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var candidates = await db.CanonicalAlbums
            .AsNoTracking()
            .Where(a => a.Status == CanonicalAlbumStatus.Fetched)
            .OrderByDescending(a => a.FetchedAtUtc)
            .Take(opts.BatchSize)
            .Select(a => new { a.Id, a.FetchedAtUtc })
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var ids = candidates.Select(c => c.Id).ToList();
        var latestByAlbum = await db.CanonicalAlbumQualityGrades
            .IgnoreQueryFilters()
            .Where(g => ids.Contains(g.CanonicalAlbumId))
            .GroupBy(g => g.CanonicalAlbumId)
            .Select(grp => grp.OrderByDescending(g => g.GradedAtUtc).First())
            .ToListAsync(ct);
        var latest = latestByAlbum.ToDictionary(g => g.CanonicalAlbumId);

        // Drop albums still inside their post-failure backoff, and prune entries that have expired.
        var now = DateTime.UtcNow;
        _failureBackoff.Prune(now);

        var needsGrading = candidates.Where(c =>
        {
            if (_failureBackoff.IsBackingOff(c.Id, now)) return false;         // backing off
            if (!latest.TryGetValue(c.Id, out var g)) return true;          // never graded
            if (c.FetchedAtUtc is { } f && g.GradedAtUtc < f) return true; // re-fetched since
            // A prompt-version or model change is NOT auto-regraded here (it would re-grade every
            // album on a config bump); such grades are surfaced as "outdated" and regraded only on an
            // explicit manual / "regrade outdated" action. See AlbumQualityEndpoints grade-outdated.
            return false;
        }).Select(c => c.Id).ToList();

        if (needsGrading.Count > 0)
        {
            channel.EnqueueRange(needsGrading, force: false);
            logger.LogInformation("Auto-grade sweep enqueued {Count} albums", needsGrading.Count);
        }

        return needsGrading.Count;
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (ct.IsCancellationRequested) break;

                await _rateGate.WaitAsync(options.CurrentValue.RequestsPerSecond, ct);
                var result = await gradingService.GradeAlbumAsync(item.CanonicalAlbumId, item.Force, ct);

                switch (result.Outcome)
                {
                    case GradeOutcome.Graded:
                        Interlocked.Exchange(ref _warnedNotConfigured, 0);
                        _failureBackoff.Clear(item.CanonicalAlbumId); // recovered — clear any backoff
                        progressTracker.IncrementGraded();
                        break;
                    case GradeOutcome.NotConfigured:
                        if (Interlocked.Exchange(ref _warnedNotConfigured, 1) == 0)
                            logger.LogWarning(
                                "Album grading is enqueued but not configured — skipping. Set QualityGrading:ApiKey to enable grading.");
                        progressTracker.IncrementSkipped();
                        break;
                    case GradeOutcome.Skipped:
                    case GradeOutcome.NotFound:
                        progressTracker.IncrementSkipped();
                        break;
                    default:
                        BackOff(item.CanonicalAlbumId);
                        progressTracker.RecordError(result.ErrorCode ?? "error", result.Error);
                        progressTracker.IncrementFailed();
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                BackOff(item.CanonicalAlbumId);
                logger.LogWarning(ex, "Album grading worker {WorkerId} failed on album {AlbumId}", workerId, item.CanonicalAlbumId);
                progressTracker.RecordError("error", ex.Message);
                progressTracker.IncrementFailed();
            }
            finally
            {
                channel.MarkProcessed();
            }
        }
    }

    /// <summary>Marks an album as recently-failed so the auto-sweep skips it for the backoff window.</summary>
    private void BackOff(int albumId) =>
        _failureBackoff.MarkFailed(albumId, TimeSpan.FromSeconds(options.CurrentValue.FailureBackoffSeconds));
}
