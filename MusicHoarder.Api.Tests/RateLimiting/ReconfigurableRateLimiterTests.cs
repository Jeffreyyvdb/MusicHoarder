using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Tests.RateLimiting;

public class ReconfigurableRateLimiterTests
{
    [Fact]
    public async Task AcquireAsync_GrantsPermitsUpToTheConfiguredBudget()
    {
        var limiter = new ReconfigurableRateLimiter();

        // TokenLimit == requestsPerSecond, so the bucket starts full: the first N acquisitions in a
        // period are granted immediately.
        for (var i = 0; i < 5; i++)
        {
            using var lease = await limiter.AcquireAsync(requestsPerSecond: 5);
            Assert.True(lease.IsAcquired);
        }
    }

    [Fact]
    public async Task AcquireAsync_StillGrantsAfterTheRateChanges()
    {
        var limiter = new ReconfigurableRateLimiter();

        using (var first = await limiter.AcquireAsync(requestsPerSecond: 3))
            Assert.True(first.IsAcquired);

        // A different rate rebuilds the underlying bucket; the fresh bucket starts full again.
        using var afterReconfigure = await limiter.AcquireAsync(requestsPerSecond: 7);
        Assert.True(afterReconfigure.IsAcquired);
    }

    [Fact]
    public async Task SeparateInstances_KeepIndependentBudgets()
    {
        // Mirrors the per-provider isolation: each service owns its own limiter, so one draining its
        // budget must not starve another. With a budget of 1 each, both first acquisitions succeed.
        var first = new ReconfigurableRateLimiter();
        var second = new ReconfigurableRateLimiter();

        using var firstLease = await first.AcquireAsync(requestsPerSecond: 1);
        using var secondLease = await second.AcquireAsync(requestsPerSecond: 1);

        Assert.True(firstLease.IsAcquired);
        Assert.True(secondLease.IsAcquired);
    }

    [Fact]
    public async Task AcquireAsync_IsSafeUnderConcurrentReconfiguration()
    {
        var limiter = new ReconfigurableRateLimiter();

        // Hammer the (re)build path from many threads with alternating rates: the lock must keep the
        // shared limiter field consistent and never throw.
        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            using var lease = await limiter.AcquireAsync(requestsPerSecond: 20 + (i % 3));
            return lease.IsAcquired;
        });

        var results = await Task.WhenAll(tasks);
        Assert.Contains(true, results);
    }
}
