namespace MusicHoarder.Api.Jobs;

/// <summary>
/// The cadence shared by background sweeps that poll the database for new work: wait an initial head
/// start, then sweep; after a sweep that found work return to the base delay, after an idle sweep
/// double the delay (capped) so an idle or disabled sweep does not re-scan the library at the base
/// cadence forever. A sweep that throws is reported through <c>onSweepFailed</c> and counts as idle;
/// cancellation ends the loop wherever it is.
/// </summary>
public static class IdleBackoffSweepLoop
{
    /// <param name="initialDelay">Head start before the first sweep (e.g. so an upstream stage has produced something to sweep).</param>
    /// <param name="baseIdleSeconds">Re-read every iteration so a config reload takes effect; values below 1 are treated as 1.</param>
    /// <param name="maxIdleSeconds">Cap on the doubled delay; never lower than the base delay.</param>
    /// <param name="sweep">Runs one sweep and returns whether it found work.</param>
    /// <param name="onSweepFailed">Receives a sweep's exception; the loop then backs off as if the sweep were idle.</param>
    /// <param name="ct">Ends the loop.</param>
    /// <param name="delay">Seam for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public static async Task RunAsync(
        TimeSpan initialDelay,
        Func<int> baseIdleSeconds,
        int maxIdleSeconds,
        Func<CancellationToken, Task<bool>> sweep,
        Action<Exception> onSweepFailed,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;

        try { await delay(initialDelay, ct); }
        catch (OperationCanceledException) { return; }

        var currentIdle = 0;
        while (!ct.IsCancellationRequested)
        {
            var baseIdle = Math.Max(1, baseIdleSeconds());
            var maxIdle = Math.Max(baseIdle, maxIdleSeconds);
            var active = false;
            try
            {
                active = await sweep(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onSweepFailed(ex);
            }

            currentIdle = NextIdleSeconds(active, baseIdle, maxIdle, currentIdle);

            try { await delay(TimeSpan.FromSeconds(currentIdle), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Reset to the base cadence when there was work; otherwise back off (doubling, capped). The
    /// first idle wait after work is the base delay, not double it.
    /// </summary>
    internal static int NextIdleSeconds(bool active, int baseIdle, int maxIdle, int currentIdle) =>
        active ? baseIdle : Math.Min(maxIdle, Math.Max(baseIdle, currentIdle * 2));
}
