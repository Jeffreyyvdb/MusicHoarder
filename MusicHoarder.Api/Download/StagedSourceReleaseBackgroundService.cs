using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Runs <see cref="StagedSourceReleaseService"/> on a slow interval when the owner has switched the
/// feature on. The gating is evaluated every tick, so flipping the runtime toggle takes effect on the
/// next tick with no restart. Stands aside while the destructive purge is running, and never overlaps
/// a manual run (the tracker is single-flight).
/// </summary>
public class StagedSourceReleaseBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<MusicEnricherOptions> options,
    StagedSourceReleaseTracker tracker,
    JobManager jobManager,
    ILogger<StagedSourceReleaseBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Staged-source release sweep failed");
            }

            var minutes = Math.Clamp(options.CurrentValue.StagedSourceReleaseSweepIntervalMinutes, 1, 1440);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var runtimeSettings = scope.ServiceProvider.GetRequiredService<IRuntimeSettingsService>();
        var effective = await runtimeSettings.GetAsync(stoppingToken);
        if (!effective.ReleaseStagedSourcesEnabled)
            return;

        if (jobManager.GetStepSnapshot(JobType.Purge).Status == "Running")
        {
            logger.LogDebug("Staged-source release sweep skipped: a purge is running");
            return;
        }

        if (!tracker.TryStart("sweep", out var jobId, out var releaseToken))
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, releaseToken);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<StagedSourceReleaseService>();
            var ownerLookup = scope.ServiceProvider.GetRequiredService<IOwnerLookupService>();
            var result = await service.ReleaseAsync(ownerLookup.OwnerUserId, linked.Token);
            tracker.Complete();
            if (result.IdleReason is null && result.Released > 0)
                logger.LogInformation("Staged-source release sweep {JobId} released {Count} files", jobId, result.Released);
        }
        catch (OperationCanceledException)
        {
            tracker.Cancelled();
            throw;
        }
        catch (Exception ex)
        {
            tracker.Fail(ex.Message);
            throw;
        }
    }
}
