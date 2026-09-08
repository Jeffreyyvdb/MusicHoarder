namespace MusicHoarder.Api.RateLimiting;

/// <summary>
/// Spaces the calls of every worker sharing one instance so that no more than the given
/// requests-per-second pass, with no bursting: each caller claims the next free slot (the later of
/// "now" and one interval after the previous slot) and waits until it. The rate is supplied per call,
/// so a reloaded option takes effect on the next request without rebuilding anything.
/// <para>
/// This is the strictly-smooth counterpart of <see cref="ReconfigurableRateLimiter"/> (a token bucket,
/// which allows a burst of up to one period's budget). Budgets are per instance: two services that
/// each hold their own gate do not share a limit, even when they read the same option.
/// </para>
/// </summary>
public sealed class RequestRateGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _nextSlotUtc = DateTime.MinValue;

    /// <summary>Waits for the next free slot at <paramref name="requestsPerSecond"/> (values below 1 are treated as 1).</summary>
    public async Task WaitAsync(int requestsPerSecond, CancellationToken ct)
    {
        var minInterval = MinInterval(requestsPerSecond);

        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var wait = _nextSlotUtc - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            var baseTime = now > _nextSlotUtc ? now : _nextSlotUtc;
            _nextSlotUtc = baseTime + minInterval;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The spacing between two slots at the given rate.</summary>
    internal static TimeSpan MinInterval(int requestsPerSecond) =>
        TimeSpan.FromSeconds(1.0 / Math.Max(1, requestsPerSecond));
}
