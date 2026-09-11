using System.Diagnostics;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Tests.RateLimiting;

public class RequestRateGateTests
{
    [Theory]
    [InlineData(4, 250)]
    [InlineData(1, 1000)]
    [InlineData(0, 1000)]   // below one request per second is clamped to one
    [InlineData(-5, 1000)]
    [InlineData(20, 50)]
    public void MinInterval_IsOneSecondOverTheRate_ClampedToAtLeastOnePerSecond(int rps, int expectedMs)
    {
        Assert.Equal(expectedMs, RequestRateGate.MinInterval(rps).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public async Task FirstCall_PassesWithoutWaiting()
    {
        var sut = new RequestRateGate();
        var clock = Stopwatch.StartNew();

        await sut.WaitAsync(requestsPerSecond: 1, CancellationToken.None);

        Assert.True(clock.ElapsedMilliseconds < 500, $"first call waited {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SequentialCalls_AreSpacedAtLeastOneIntervalApart()
    {
        var sut = new RequestRateGate();
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < 4; i++)
            await sut.WaitAsync(requestsPerSecond: 20, CancellationToken.None);

        // Four slots at 50ms spacing: the last one cannot open before 150ms.
        Assert.True(clock.ElapsedMilliseconds >= 140, $"four calls took only {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneBudget()
    {
        var sut = new RequestRateGate();
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sut.WaitAsync(requestsPerSecond: 20, CancellationToken.None)));

        Assert.True(clock.ElapsedMilliseconds >= 140, $"four concurrent calls took only {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TwoGates_DoNotShareABudget()
    {
        var a = new RequestRateGate();
        var b = new RequestRateGate();
        var clock = Stopwatch.StartNew();

        await a.WaitAsync(requestsPerSecond: 1, CancellationToken.None);
        await b.WaitAsync(requestsPerSecond: 1, CancellationToken.None);

        Assert.True(clock.ElapsedMilliseconds < 500, $"a second gate waited on the first: {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Cancellation_WhileWaitingForASlot_Throws()
    {
        var sut = new RequestRateGate();
        await sut.WaitAsync(requestsPerSecond: 1, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.WaitAsync(requestsPerSecond: 1, cts.Token));
    }
}
