using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// A rolling-window meter for transcription <b>audio-seconds</b>, so the timing probe stays inside a free
/// provider tier instead of discovering the limit as a wall of 429s.
///
/// Groq's free whisper tier is quota'd on seconds of audio submitted per hour and per day, not on requests,
/// which is what makes a 30-second probe roughly a seventh the cost of transcribing a whole song. The meter
/// is deliberately in-memory and per-process: it is a courtesy throttle in front of a limit the provider
/// enforces anyway, not an accounting record, so losing it on restart is harmless — the worst case is one
/// window's worth of over-spend that the provider itself rejects.
/// </summary>
public sealed class LyricsProbeBudget(IOptionsMonitor<LyricsTimingOptions> options, ILogger<LyricsProbeBudget> logger)
{
    private readonly object _gate = new();
    private readonly Queue<(DateTime AtUtc, double Seconds)> _spend = new();

    /// <summary>
    /// Reserves <paramref name="audioSeconds"/> against both the hourly and the daily allowance, returning
    /// false when either is exhausted. Reserved up front rather than recorded afterwards, so several
    /// concurrent probes cannot each see room that only one of them has.
    /// </summary>
    public bool TryReserve(double audioSeconds)
    {
        var opts = options.CurrentValue;
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            Prune(now);

            var hour = _spend.Where(e => e.AtUtc > now.AddHours(-1)).Sum(e => e.Seconds);
            var day = _spend.Sum(e => e.Seconds);

            if (hour + audioSeconds > opts.AudioSecondsPerHour)
            {
                logger.LogDebug(
                    "Lyrics probe budget: hourly allowance spent ({Spent:F0}/{Limit}s of audio).",
                    hour, opts.AudioSecondsPerHour);
                return false;
            }

            if (day + audioSeconds > opts.AudioSecondsPerDay)
            {
                logger.LogDebug(
                    "Lyrics probe budget: daily allowance spent ({Spent:F0}/{Limit}s of audio).",
                    day, opts.AudioSecondsPerDay);
                return false;
            }

            _spend.Enqueue((now, audioSeconds));
            return true;
        }
    }

    /// <summary>Hands back a reservation whose request never reached the provider (a local failure).</summary>
    public void Refund(double audioSeconds)
    {
        lock (_gate)
        {
            // Reservations are uniform in size, so dropping the newest matching entry is exact.
            var kept = new List<(DateTime, double)>(_spend.Count);
            var refunded = false;
            while (_spend.Count > 0)
            {
                var entry = _spend.Dequeue();
                if (!refunded && Math.Abs(entry.Seconds - audioSeconds) < 0.001)
                {
                    refunded = true;
                    continue;
                }
                kept.Add(entry);
            }
            foreach (var entry in kept)
                _spend.Enqueue(entry);
        }
    }

    /// <summary>Audio-seconds still available this hour and today — surfaced for diagnostics.</summary>
    public (double Hour, double Day) Remaining()
    {
        var opts = options.CurrentValue;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            Prune(now);
            var hour = _spend.Where(e => e.AtUtc > now.AddHours(-1)).Sum(e => e.Seconds);
            var day = _spend.Sum(e => e.Seconds);
            return (Math.Max(0, opts.AudioSecondsPerHour - hour), Math.Max(0, opts.AudioSecondsPerDay - day));
        }
    }

    /// <summary>Drops entries older than the widest window (a day); callers already hold the lock.</summary>
    private void Prune(DateTime now)
    {
        var cutoff = now.AddDays(-1);
        while (_spend.Count > 0 && _spend.Peek().AtUtc <= cutoff)
            _spend.Dequeue();
    }
}
