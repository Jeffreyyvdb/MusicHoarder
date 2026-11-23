using System.Net;

namespace MusicHoarder.Api.Enrichment;

internal readonly record struct GeniusRefusalTrip(int ConsecutiveRefusals, HttpStatusCode LastStatus, DateTimeOffset Until);

internal sealed class GeniusRefusalGate
{
    private const int RefusalsToTrip = 5;
    private static readonly TimeSpan FirstCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(24);

    private readonly Lock _lock = new();
    private int _consecutiveRefusals;
    private TimeSpan _lastCooldown = TimeSpan.Zero;
    private DateTimeOffset _closedUntil = DateTimeOffset.MinValue;

    // Cloudflare answers a refused address with 403 (a challenge or block) or 429 (a rate limit rule).
    public static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    public bool IsClosed(DateTimeOffset now)
    {
        lock (_lock) return now < _closedUntil;
    }

    public void Answered()
    {
        lock (_lock)
        {
            _consecutiveRefusals = 0;
            _lastCooldown = TimeSpan.Zero;
        }
    }

    public GeniusRefusalTrip? Refused(HttpStatusCode status, TimeSpan? retryAfter, DateTimeOffset now)
    {
        lock (_lock)
        {
            _consecutiveRefusals++;
            if (_consecutiveRefusals < RefusalsToTrip || now < _closedUntil)
                return null;

            _lastCooldown = _lastCooldown == TimeSpan.Zero
                ? FirstCooldown
                : TimeSpan.FromTicks(Math.Min(_lastCooldown.Ticks * 2, MaxCooldown.Ticks));
            _closedUntil = now + (retryAfter > _lastCooldown ? retryAfter.Value : _lastCooldown);
            return new GeniusRefusalTrip(_consecutiveRefusals, status, _closedUntil);
        }
    }
}
