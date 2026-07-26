using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Pipeline;

namespace MusicHoarder.Api.Scanner;

/// <summary>
/// Re-scans the source library on a timer so files dropped onto the share are picked up without
/// anyone clicking Scan. <see cref="DirectoryAvailabilityMonitor"/> only triggers a scan on startup
/// and on the source offline→online <em>edge</em>, so a copy into an already-online library would
/// otherwise stay invisible until the next restart.
///
/// Deliberately not gated on <see cref="MusicEnricherOptions.AutoStartPipeline"/>: that flag gates
/// <em>processing</em>, and discovery scanning is ungated by design (see its doc comment). A scan that
/// finds nothing is a dead end anyway — <see cref="ScannerBackgroundService"/> only cascades into
/// fingerprint when the index reports new or changed files.
/// </summary>
public sealed class AutoScanBackgroundService(
    JobManager jobManager,
    IDirectoryAvailability directoryAvailability,
    IOptionsMonitor<MusicEnricherOptions> options,
    ILogger<AutoScanBackgroundService> logger) : BackgroundService
{
    // Let startup work (migrations, demo seeding) and the startup scan settle first.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.CurrentValue.AutoScanIntervalMinutes <= 0)
        {
            logger.LogInformation("Automatic source re-scan disabled (AutoScanIntervalMinutes=0)");
            return;
        }

        logger.LogInformation(
            "Automatic source re-scan started. Interval={IntervalMinutes}m",
            options.CurrentValue.AutoScanIntervalMinutes);

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
                Tick();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic source re-scan tick failed");
            }

            // Re-read each tick so the interval can be retuned without a restart. A value of 0 parks
            // the loop on the shortest allowed delay rather than exiting, so re-enabling also takes
            // effect live.
            var minutes = options.CurrentValue.AutoScanIntervalMinutes;
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes > 0 ? minutes : 1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One scheduling decision. Separated from the polling loop so it can be unit-tested directly.
    /// Returns true when a scan job was started.
    /// </summary>
    internal bool Tick()
    {
        if (options.CurrentValue.AutoScanIntervalMinutes <= 0)
            return false;

        // An unreachable share would only produce a scan that bails out and re-probes; leave that to
        // the availability monitor, which re-triggers a scan on the reconnect edge anyway.
        if (!directoryAvailability.Current.SourceAvailable)
            return false;

        // TryStartJob clears IsPaused because it models a user-initiated action — so check the flag
        // first, or a paused Scan step would silently un-pause itself every interval. (TryRegisterAutoJob
        // respects the flag but never writes the trigger channel, and ScannerBackgroundService is a pure
        // channel consumer, so using it here would strand the step at Running forever.)
        if (jobManager.IsStepPaused(JobType.Scan))
            return false;

        // A false return just means a scan is already running: skip this tick, no queueing needed.
        if (!jobManager.TryStartJob(JobType.Scan, out var jobId, out _))
            return false;

        logger.LogInformation(
            "Auto-triggered periodic scan {ScanJobId} of {SourceDirectory}",
            jobId, options.CurrentValue.SourceDirectory);
        return true;
    }
}
