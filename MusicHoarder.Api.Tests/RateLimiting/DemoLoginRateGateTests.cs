using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Tests.RateLimiting;

public class DemoLoginRateGateTests
{
    [Fact]
    public void First_attempts_in_a_window_pass_then_reject_until_it_rolls()
    {
        var gate = new DemoLoginRateGate(2);

        Assert.True(gate.TryAcquire(out var firstRetryAfter));
        Assert.Equal(TimeSpan.Zero, firstRetryAfter);
        Assert.True(gate.TryAcquire(out _));
        Assert.False(gate.TryAcquire(out var retryAfter));
        Assert.InRange(retryAfter.TotalSeconds, 0.0, 60.0);
        Assert.False(gate.TryAcquire(out _));
    }

    [Fact]
    public void A_permit_limit_below_one_still_admits_one_attempt_per_window()
    {
        var gate = new DemoLoginRateGate(0);

        Assert.True(gate.TryAcquire(out _));
        Assert.False(gate.TryAcquire(out _));
    }
}
