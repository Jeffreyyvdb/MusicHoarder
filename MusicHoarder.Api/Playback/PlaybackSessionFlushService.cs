namespace MusicHoarder.Api.Playback;

/// <summary>
/// Keeps <see cref="PlaybackCoordinator"/> honest over time and durable across restarts. Every few
/// seconds it sweeps (reachability expires on its own, so a device that went quiet has to be
/// noticed without anyone asking) and writes the sessions that changed since the last tick — one
/// batched write instead of one per position report. On shutdown it writes once more, including
/// every session still playing, so the remembered position is as fresh as it can be.
/// </summary>
public sealed class PlaybackSessionFlushService(
    PlaybackCoordinator coordinator,
    IPlaybackSessionStore store,
    TimeProvider time,
    ILogger<PlaybackSessionFlushService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The final write's own budget. The host's stop token is shared by everything that stops
    /// before this service and may well have run out by the time it gets here; a write started on
    /// it would be cancelled on its first query.
    /// </summary>
    internal static readonly TimeSpan FinalFlushTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    coordinator.Sweep();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Playback sweep failed");
                }

                await FlushAsync(includePlaying: false, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; StopAsync does the final flush.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        using var timeout = new CancellationTokenSource(FinalFlushTimeout, time);
        await FlushAsync(includePlaying: true, timeout.Token);
    }

    /// <summary>Writes what changed. A failed write hands the sessions back, to be retried next tick.</summary>
    internal async Task FlushAsync(bool includePlaying, CancellationToken ct)
    {
        var sessions = coordinator.TakeDirty(includePlaying);
        if (sessions.Count == 0) return;

        try
        {
            await store.SaveAsync(sessions, ct);
            logger.LogDebug("Saved {Count} playback session(s)", sessions.Count);
        }
        catch (Exception ex)
        {
            coordinator.MarkDirty(sessions.Select(s => s.OwnerUserId));
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                logger.LogWarning("Saving {Count} playback session(s) was cancelled at shutdown", sessions.Count);
            else
                logger.LogWarning(ex, "Saving {Count} playback session(s) failed; will retry", sessions.Count);
        }
    }
}
