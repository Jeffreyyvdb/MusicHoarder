using System.Threading.RateLimiting;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.AppleMusic;

/// <summary>
/// The one pace and backoff window for every call to <c>itunes.apple.com</c>. iTunes throttles by IP,
/// so the enrichment catalog and the clean-edit check share <c>MusicEnricher:AppleMusicApiRequestsPerMinute</c>,
/// and once one caller is rate limited every caller waits out the same window.
/// </summary>
internal static class ITunesThrottle
{
    /// <summary>Without a Retry-After header, assume the per-minute budget is spent.</summary>
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(60);

    private static readonly ReconfigurableRateLimiter Limiter = new();
    private static long _backoffUntilUtcTicks;

    /// <summary>
    /// A permit to call iTunes now. Throws <see cref="ProviderRateLimitedException"/> when a backoff
    /// window opened while the caller waited in the queue for the permit.
    /// </summary>
    public static async ValueTask<RateLimitLease> AcquireAsync(MusicEnricherOptions options, CancellationToken ct)
    {
        var interval = PaceForTests ?? TimeSpan.FromSeconds(60.0 / Math.Clamp(options.AppleMusicApiRequestsPerMinute, 1, 30));
        var lease = await Limiter.AcquireAsync(1, interval, ct);
        var wait = BackoffUntil - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero)
            return lease;
        lease.Dispose();
        throw new ProviderRateLimitedException(wait);
    }

    public static DateTime BackoffUntil => new(Interlocked.Read(ref _backoffUntilUtcTicks), DateTimeKind.Utc);

    public static void ExtendBackoff(TimeSpan retryAfter)
    {
        var candidate = (DateTime.UtcNow + retryAfter).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _backoffUntilUtcTicks);
            if (candidate <= current)
                return;
        } while (Interlocked.CompareExchange(ref _backoffUntilUtcTicks, candidate, current) != current);
    }

    internal static void ResetBackoffForTests() => Interlocked.Exchange(ref _backoffUntilUtcTicks, 0);

    /// <summary>Tests that never reach iTunes need not wait out its real pace.</summary>
    internal static TimeSpan? PaceForTests { get; set; }
}
