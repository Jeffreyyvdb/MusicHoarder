using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Admission control for the transcription endpoint: at most N requests per rolling minute, plus a
/// shared cooldown whenever the provider tells us to back off.
///
/// This sits at the HTTP layer rather than in <see cref="LyricsProbeBudget"/> on purpose. Providers meter
/// transcription two ways at once — audio-seconds AND requests — and the two bind at completely different
/// times: thirty-second probe windows exhaust a 20/minute request allowance while barely denting an
/// hourly audio budget. A limiter that only counts one of them lets the other run straight into 429s.
/// It also has to sit BELOW the retry loop, because a retry is another request against the provider's
/// count; a caller-level gate would meter one attempt and then fire three.
///
/// Waiting is the correct response to being near the limit — the alternative is a request the provider
/// rejects, which costs the same round trip and yields nothing. Waits are bounded so an interactive
/// transcription cannot hang indefinitely behind a saturated background sweep.
/// </summary>
public sealed class LyricsTranscriptionRateLimiter(
    IOptionsMonitor<LyricsTranscriptionOptions> options,
    ILogger<LyricsTranscriptionRateLimiter> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTime> _recent = new();

    /// <summary>
    /// When the provider last told us to stop. Shared across every caller: a 429 is a statement about the
    /// whole organisation's quota, so one worker discovering it must slow all of them down, not just itself.
    /// </summary>
    private DateTime _notBeforeUtc = DateTime.MinValue;

    /// <summary>
    /// Waits for a request slot. Returns false when the wait would exceed <paramref name="maxWait"/>, which
    /// callers must treat as "we did not call the provider" — never as a result.
    /// </summary>
    public async Task<bool> TryAcquireAsync(TimeSpan maxWait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxWait;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan wait;
            await _gate.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                Prune(now);

                var limit = Math.Max(1, options.CurrentValue.RequestsPerMinute);
                var cooldown = _notBeforeUtc > now ? _notBeforeUtc - now : TimeSpan.Zero;

                if (cooldown <= TimeSpan.Zero && _recent.Count < limit)
                {
                    _recent.Enqueue(now);
                    return true;
                }

                // How long until the oldest request ages out of the rolling minute, or the cooldown lifts.
                var untilSlot = _recent.Count >= limit
                    ? _recent.Peek().AddMinutes(1) - now
                    : TimeSpan.Zero;
                wait = cooldown > untilSlot ? cooldown : untilSlot;
                if (wait < TimeSpan.FromMilliseconds(50))
                    wait = TimeSpan.FromMilliseconds(50);
            }
            finally
            {
                _gate.Release();
            }

            if (DateTime.UtcNow + wait > deadline)
            {
                logger.LogDebug(
                    "Transcription rate limiter: a slot is more than {MaxWait}s away; not calling the provider.",
                    maxWait.TotalSeconds);
                return false;
            }

            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Records a provider back-off instruction (a 429's <c>Retry-After</c>, or a default when it sends none),
    /// pausing every caller until it elapses.
    /// </summary>
    public void NoteBackoff(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
            return;

        // Cap it: a provider that asks for an implausibly long pause must not wedge the pipeline for hours.
        if (retryAfter > TimeSpan.FromMinutes(5))
            retryAfter = TimeSpan.FromMinutes(5);

        var until = DateTime.UtcNow + retryAfter;
        _gate.Wait();
        try
        {
            if (until > _notBeforeUtc)
            {
                _notBeforeUtc = until;
                logger.LogInformation(
                    "Transcription provider asked us to back off for {Seconds:F0}s; pausing all transcription until then.",
                    retryAfter.TotalSeconds);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// How long the provider-imposed cooldown still has to run, or zero when there is none. Exposed for
    /// diagnostics — and so the cap on an absurd Retry-After can be asserted without a test that actually
    /// sits out the cooldown.
    /// </summary>
    public TimeSpan BackoffRemaining
    {
        get
        {
            var remaining = _notBeforeUtc - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Drops request stamps older than the rolling minute. Callers already hold the gate.</summary>
    private void Prune(DateTime now)
    {
        var cutoff = now.AddMinutes(-1);
        while (_recent.Count > 0 && _recent.Peek() <= cutoff)
            _recent.Dequeue();
    }
}
