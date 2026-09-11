using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Storage;

/// <summary>
/// Measures on-disk storage usage on a slow interval and whenever <see cref="StorageUsageSnapshotStore"/>
/// is asked for a refresh. The first sidebar load on a fresh instance requests one, so the startup
/// delay only holds when nobody is looking. A single owner of the compute keeps the walk of a large
/// SMB share from ever overlapping itself.
/// </summary>
public class StorageUsageBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<MusicEnricherOptions> options,
    StorageUsageSnapshotStore store,
    ILogger<StorageUsageBackgroundService> logger) : BackgroundService
{
    /// <summary>Lets the directory monitor take its first probe and startup scans settle.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitForRequestOrDelayAsync(InitialDelay, stoppingToken))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await MeasureOnceAsync(stoppingToken);

            var minutes = options.CurrentValue.StorageUsageRefreshIntervalMinutes;
            var delay = minutes <= 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 10080));
            if (!await WaitForRequestOrDelayAsync(delay, stoppingToken))
                return;
        }
    }

    /// <summary>Waits for a refresh request or the interval, whichever comes first. False when the host is stopping.</summary>
    private async Task<bool> WaitForRequestOrDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (delay != Timeout.InfiniteTimeSpan)
            timeout.CancelAfter(delay);
        try
        {
            await store.WaitForRequestAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return !stoppingToken.IsCancellationRequested;
        }
    }

    private async Task MeasureOnceAsync(CancellationToken stoppingToken)
    {
        if (!store.TryBegin())
            return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var calculator = scope.ServiceProvider.GetRequiredService<StorageUsageCalculator>();
            var snapshot = await calculator.ComputeAsync(stoppingToken);
            store.Complete(snapshot);
            logger.LogInformation(
                "Storage usage measured: {ManagedBytes} bytes across {Roots} roots in {Ms} ms",
                snapshot.ManagedBytes, snapshot.Roots.Count, snapshot.DurationMs);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            store.Fail("Measurement was cancelled at shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Storage usage measurement failed");
            store.Fail(ex.Message);
        }
    }
}
