using System.Threading.RateLimiting;

namespace MusicHoarder.Api.RateLimiting;

/// <summary>
/// A thread-safe token-bucket rate limiter whose throughput is keyed off a configurable
/// permits-per-period budget. The first acquisition (and any acquisition after the configured
/// rate changes) lazily rebuilds the underlying <see cref="TokenBucketRateLimiter"/>; otherwise the
/// existing limiter is reused so callers share a single token budget.
/// <para>
/// Each outbound provider (AcoustID, MusicBrainz, Spotify, Deezer, Apple Music) holds its own
/// <c>static</c> instance so the limiter survives the transient, <c>HttpClient</c>-injected service
/// instances while keeping each provider's budget isolated from the others. This replaces the
/// hand-rolled <c>lock</c> + shared-static-limiter block that was duplicated verbatim across every
/// catalog/lookup service.
/// </para>
/// <para>
/// Most providers cap requests-per-second, but some APIs (e.g. the iTunes Search API, ~20/min) are
/// throttled on a sub-1-rps basis. The period-aware overload lets those callers express, say,
/// "1 permit per 4s" — a strictly smooth rate the per-second form cannot represent.
/// </para>
/// </summary>
public sealed class ReconfigurableRateLimiter
{
    private readonly object _gate = new();
    private TokenBucketRateLimiter? _limiter;
    private int _permitsPerPeriod = -1;
    private TimeSpan _period = TimeSpan.Zero;

    /// <summary>
    /// Acquires a single permit at the given requests-per-second budget, (re)building the underlying
    /// limiter when the rate changes. Mirrors the previous call sites: callers <c>using</c> the
    /// returned lease and check <see cref="RateLimitLease.IsAcquired"/>.
    /// </summary>
    public ValueTask<RateLimitLease> AcquireAsync(int requestsPerSecond, CancellationToken ct = default)
        => AcquireAsync(requestsPerSecond, TimeSpan.FromSeconds(1), ct);

    /// <summary>
    /// Acquires a single permit against a token bucket that replenishes <paramref name="permitsPerPeriod"/>
    /// tokens every <paramref name="period"/>. Use <c>permitsPerPeriod: 1</c> with a multi-second period
    /// for a strictly smooth, burst-free rate (e.g. one request every 4s for ~15/min).
    /// </summary>
    public ValueTask<RateLimitLease> AcquireAsync(int permitsPerPeriod, TimeSpan period, CancellationToken ct = default)
        => GetOrCreate(permitsPerPeriod, period).AcquireAsync(permitCount: 1, ct);

    private TokenBucketRateLimiter GetOrCreate(int permitsPerPeriod, TimeSpan period)
    {
        lock (_gate)
        {
            if (_limiter is not null && _permitsPerPeriod == permitsPerPeriod && _period == period)
                return _limiter;

            _limiter?.Dispose();
            _permitsPerPeriod = permitsPerPeriod;
            _period = period;
            _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = permitsPerPeriod,
                TokensPerPeriod = permitsPerPeriod,
                ReplenishmentPeriod = period,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = int.MaxValue,
                AutoReplenishment = true,
            });
            return _limiter;
        }
    }
}
