using System.Threading.RateLimiting;

namespace MusicHoarder.Api.RateLimiting;

/// <summary>
/// A thread-safe token-bucket rate limiter whose throughput is keyed off a configurable
/// requests-per-second value. The first acquisition (and any acquisition after the configured
/// rate changes) lazily rebuilds the underlying <see cref="TokenBucketRateLimiter"/>; otherwise the
/// existing limiter is reused so callers share a single token budget.
/// <para>
/// Each outbound provider (AcoustID, MusicBrainz, Spotify, Deezer, Apple Music) holds its own
/// <c>static</c> instance so the limiter survives the transient, <c>HttpClient</c>-injected service
/// instances while keeping each provider's budget isolated from the others. This replaces the
/// hand-rolled <c>lock</c> + shared-static-limiter block that was duplicated verbatim across every
/// catalog/lookup service.
/// </para>
/// </summary>
public sealed class ReconfigurableRateLimiter
{
    private readonly object _gate = new();
    private TokenBucketRateLimiter? _limiter;
    private int _ratePerSecond = -1;

    /// <summary>
    /// Acquires a single permit at the given requests-per-second budget, (re)building the underlying
    /// limiter when the rate changes. Mirrors the previous call sites: callers <c>using</c> the
    /// returned lease and check <see cref="RateLimitLease.IsAcquired"/>.
    /// </summary>
    public ValueTask<RateLimitLease> AcquireAsync(int requestsPerSecond, CancellationToken ct = default)
        => GetOrCreate(requestsPerSecond).AcquireAsync(permitCount: 1, ct);

    private TokenBucketRateLimiter GetOrCreate(int requestsPerSecond)
    {
        lock (_gate)
        {
            if (_limiter is not null && _ratePerSecond == requestsPerSecond)
                return _limiter;

            _limiter?.Dispose();
            _ratePerSecond = requestsPerSecond;
            _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = requestsPerSecond,
                TokensPerPeriod = requestsPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = int.MaxValue,
                AutoReplenishment = true,
            });
            return _limiter;
        }
    }
}
