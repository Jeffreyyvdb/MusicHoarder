using System.Net;

namespace MusicHoarder.Api.Enrichment;

/// <summary>A run of refusals that closed the gate, for the one warning it is worth.</summary>
internal readonly record struct GeniusRefusalTrip(int ConsecutiveRefusals, HttpStatusCode LastStatus, DateTimeOffset Until);

/// <summary>
/// Stops asking Genius once it keeps refusing. Cloudflare answers a refused address with 403 or 429 to
/// every request, so after <see cref="RefusalsToTrip"/> in a row the gate closes for an hour, doubling on
/// each further trip up to a day, and never for less than a <c>Retry-After</c> asked for. Once it
/// reopens, the next refusal closes it again at once with the doubled cool-down; any 2xx answer starts
/// over. Other failures (a 5xx, a timeout) say nothing about being refused and change nothing.
/// </summary>
internal sealed class GeniusRefusalGate
{
    private const int RefusalsToTrip = 5;
    private static readonly TimeSpan FirstCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(24);

    private readonly Lock _lock = new();
    private int _consecutiveRefusals;
    private TimeSpan _lastCooldown = TimeSpan.Zero;
    private DateTimeOffset _closedUntil = DateTimeOffset.MinValue;

    public static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    public bool IsClosed(DateTimeOffset now)
    {
        lock (_lock) return now < _closedUntil;
    }

    /// <summary>Records one answer; returns the trip when this answer closed the gate.</summary>
    public GeniusRefusalTrip? Record(HttpStatusCode status, TimeSpan? retryAfter, DateTimeOffset now)
    {
        lock (_lock)
        {
            if ((int)status is >= 200 and < 300)
            {
                _consecutiveRefusals = 0;
                _lastCooldown = TimeSpan.Zero;
                return null;
            }

            if (!IsRefusal(status))
                return null;

            _consecutiveRefusals++;
            // Requests already in flight when the gate closed must not each double the cool-down.
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
