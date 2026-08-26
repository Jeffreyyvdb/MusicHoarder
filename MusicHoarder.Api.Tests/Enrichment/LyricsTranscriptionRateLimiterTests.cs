using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Local admission control for the transcription endpoint.
///
/// This exists because of a real production failure: the probe budget metered audio-seconds only, so a
/// sweep of thirty-second windows sailed past an hourly audio budget while running head-first into the
/// provider's 20-requests-per-minute ceiling. Both meters have to be honoured, and the request one has to
/// sit below the retry loop, because a retry is another request against the provider's count.
/// </summary>
public class LyricsTranscriptionRateLimiterTests
{
    private static LyricsTranscriptionRateLimiter New(LyricsTranscriptionOptions opts) =>
        new(new StaticOptionsMonitor<LyricsTranscriptionOptions>(opts),
            NullLogger<LyricsTranscriptionRateLimiter>.Instance);

    [Fact]
    public async Task admits_requests_up_to_the_per_minute_allowance()
    {
        var limiter = New(new LyricsTranscriptionOptions { RequestsPerMinute = 3 });

        for (var i = 0; i < 3; i++)
            Assert.True(await limiter.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task refuses_rather_than_overshooting_the_allowance()
    {
        // The whole point: the 21st request in a minute must not leave the process. Being told "no" costs
        // nothing, while sending it costs a round trip and a 429.
        var limiter = New(new LyricsTranscriptionOptions { RequestsPerMinute = 2 });

        Assert.True(await limiter.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.True(await limiter.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await limiter.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task waits_for_a_slot_when_it_is_allowed_to()
    {
        // With room to wait, the answer is "shortly" rather than "no" — a background sweep would rather
        // pause a moment than come back through the whole database again.
        var limiter = New(new LyricsTranscriptionOptions { RequestsPerMinute = 1 });
        Assert.True(await limiter.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None));

        // The slot is a minute away, so even a generous-but-shorter wait must decline rather than block.
        var start = DateTime.UtcNow;
        Assert.False(await limiter.TryAcquireAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(5), "declining must be prompt, not a stall");
    }

    [Fact]
    public async Task a_provider_backoff_pauses_callers_that_still_have_slots()
    {
        // A 429 is a statement about the whole organisation's quota, so one worker learning about it has to
        // slow every worker down — not just itself, or the others walk straight into the same wall.
        var limiter = New(new LyricsTranscriptionOptions { RequestsPerMinute = 50 });

        limiter.NoteBackoff(TimeSpan.FromMinutes(2));

        Assert.False(await limiter.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public void an_absurd_backoff_instruction_cannot_wedge_the_pipeline()
    {
        var limiter = New(new LyricsTranscriptionOptions { RequestsPerMinute = 50 });

        limiter.NoteBackoff(TimeSpan.FromHours(9));

        // Capped at five minutes, so a misbehaving provider costs us minutes rather than the rest of the
        // day. Asserted on the remaining cooldown rather than by waiting it out — a test that actually sits
        // through the pause would be a five-minute test.
        Assert.True(limiter.BackoffRemaining <= TimeSpan.FromMinutes(5));
        Assert.True(limiter.BackoffRemaining > TimeSpan.FromMinutes(4));
    }

    [Fact]
    public void a_longer_backoff_extends_the_pause_and_a_shorter_one_does_not_shorten_it()
    {
        // Two workers can discover the limit at once; the pause must settle on the later instant, or the
        // second (shorter) report would release everyone early and start the storm again.
        var limiter = New(new LyricsTranscriptionOptions { RequestsPerMinute = 50 });

        limiter.NoteBackoff(TimeSpan.FromMinutes(3));
        limiter.NoteBackoff(TimeSpan.FromSeconds(5));

        Assert.True(limiter.BackoffRemaining > TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void a_zero_or_negative_backoff_is_ignored()
    {
        var limiter = New(new LyricsTranscriptionOptions { RequestsPerMinute = 50 });

        limiter.NoteBackoff(TimeSpan.Zero);
        limiter.NoteBackoff(TimeSpan.FromSeconds(-5));

        Assert.True(limiter.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None).Result);
    }
}
