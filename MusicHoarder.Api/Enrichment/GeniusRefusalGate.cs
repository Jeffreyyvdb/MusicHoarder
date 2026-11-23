using System.Net;

namespace MusicHoarder.Api.Enrichment;

internal enum GeniusRequest
{
    Search,
    SongPage,
}

internal readonly record struct GeniusRefusalTrip(
    GeniusRequest Request, int ConsecutiveRefusals, HttpStatusCode LastStatus, DateTimeOffset Until);

/// <summary>
/// Counts refusals per kind of request, so Cloudflare blocking only song pages trips the gate even
/// while every search is answered.
/// </summary>
internal sealed class GeniusRefusalGate
{
    private const int RefusalsToTrip = 5;
    private static readonly TimeSpan FirstCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(24);

    private readonly Lock _lock = new();
    private readonly Dictionary<GeniusRequest, int> _consecutiveRefusals = [];
    private TimeSpan _lastCooldown = TimeSpan.Zero;
    private DateTimeOffset _closedUntil = DateTimeOffset.MinValue;

    // Cloudflare answers a refused address with 403 (a challenge or block) or 429 (a rate limit rule).
    public static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    public bool IsClosed(DateTimeOffset now)
    {
        lock (_lock) return now < _closedUntil;
    }

    public void Answered(GeniusRequest request)
    {
        lock (_lock)
        {
            _consecutiveRefusals.Remove(request);
            if (_consecutiveRefusals.Count == 0)
                _lastCooldown = TimeSpan.Zero;
        }
    }

    public GeniusRefusalTrip? Refused(GeniusRequest request, HttpStatusCode status, TimeSpan? retryAfter, DateTimeOffset now)
    {
        lock (_lock)
        {
            var refusals = _consecutiveRefusals[request] = _consecutiveRefusals.GetValueOrDefault(request) + 1;
            var askedToWait = TimeSpan.FromTicks(Math.Clamp((retryAfter ?? TimeSpan.Zero).Ticks, 0, MaxCooldown.Ticks));
            var sentBeforeTheGateClosed = now < _closedUntil;
            if (refusals < RefusalsToTrip || sentBeforeTheGateClosed)
            {
                if (now + askedToWait > _closedUntil)
                    _closedUntil = now + askedToWait;
                return null;
            }

            _lastCooldown = _lastCooldown == TimeSpan.Zero
                ? FirstCooldown
                : TimeSpan.FromTicks(Math.Min(_lastCooldown.Ticks * 2, MaxCooldown.Ticks));
            _closedUntil = now + (askedToWait > _lastCooldown ? askedToWait : _lastCooldown);
            return new GeniusRefusalTrip(request, refusals, status, _closedUntil);
        }
    }
}
