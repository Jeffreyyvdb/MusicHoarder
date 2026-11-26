namespace MusicHoarder.Api.RateLimiting;

/// <summary>
/// Caps demo-logins per rolling minute for the whole instance: the first attempts in a window
/// succeed, the rest answer 429 until the window rolls. Anonymous and instance-wide on purpose —
/// the demo login belongs to no account whose window could cap it (magic links are limited per
/// account in the database for exactly that reason), and the API cannot key on a client address
/// because it only ever sees the proxy in front of it. See
/// <see cref="Auth.DemoLoginLimitPerMinute"/> in <c>AuthOptions</c>.
/// </summary>
public sealed class DemoLoginRateGate
{
    private readonly object _lock = new();
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private DateTime _windowStartUtc = DateTime.MinValue;
    private int _count;

    public DemoLoginRateGate(int permitsPerMinute)
    {
        _permitLimit = Math.Max(1, permitsPerMinute);
        _window = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Claims one demo-login slot. False means the instance is over the limit; the out value is
    /// how long until the current window rolls (never zero, so a Retry-After header is always
    /// expressible).
    /// </summary>
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_windowStartUtc == DateTime.MinValue || now - _windowStartUtc >= _window)
            {
                _windowStartUtc = now;
                _count = 0;
            }

            if (_count < _permitLimit)
            {
                _count++;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            var remainder = _windowStartUtc + _window - now;
            retryAfter = remainder > TimeSpan.Zero ? remainder : TimeSpan.FromSeconds(1);
            return false;
        }
    }
}
