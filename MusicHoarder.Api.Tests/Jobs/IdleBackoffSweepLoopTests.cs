using MusicHoarder.Api.Jobs;

namespace MusicHoarder.Api.Tests.Jobs;

public class IdleBackoffSweepLoopTests
{
    [Theory]
    [InlineData(true, 30, 300, 0, 30)]      // work found → base cadence
    [InlineData(true, 30, 300, 240, 30)]    // work found after a long idle → straight back to base
    [InlineData(false, 30, 300, 0, 30)]     // first idle wait is the base delay, not double it
    [InlineData(false, 30, 300, 30, 60)]    // then doubles
    [InlineData(false, 30, 300, 120, 240)]
    [InlineData(false, 30, 300, 240, 300)]  // capped
    [InlineData(false, 30, 300, 300, 300)]  // stays capped
    [InlineData(false, 400, 400, 0, 400)]   // a base above the cap: the cap was raised to the base
    public void NextIdleSeconds_ResetsOnWork_AndDoublesCappedWhenIdle(
        bool active, int baseIdle, int maxIdle, int currentIdle, int expected)
    {
        Assert.Equal(expected, IdleBackoffSweepLoop.NextIdleSeconds(active, baseIdle, maxIdle, currentIdle));
    }

    [Fact]
    public async Task RunAsync_WaitsTheInitialDelay_ThenSweepsOnTheBackoffCadence()
    {
        var harness = new Harness(sweeps: [false, false, false, true, false]);

        await harness.RunAsync(initialDelay: TimeSpan.FromSeconds(10), baseIdleSeconds: 30, maxIdleSeconds: 300);

        Assert.Equal(5, harness.SweepCalls);
        // Head start, then: idle 30 → 60 → 120, work resets to 30, idle 60.
        Assert.Equal([10, 30, 60, 120, 30, 60], harness.Delays);
    }

    [Fact]
    public async Task RunAsync_CapsTheIdleDelay_AndNeverBelowTheBase()
    {
        var harness = new Harness(sweeps: [false, false, false, false, false, false]);

        await harness.RunAsync(initialDelay: TimeSpan.Zero, baseIdleSeconds: 30, maxIdleSeconds: 300);

        Assert.Equal([0, 30, 60, 120, 240, 300, 300], harness.Delays);
    }

    [Fact]
    public async Task RunAsync_TreatsABaseBelowOneSecond_AsOneSecond()
    {
        var harness = new Harness(sweeps: [false, false]);

        await harness.RunAsync(initialDelay: TimeSpan.Zero, baseIdleSeconds: 0, maxIdleSeconds: 300);

        Assert.Equal([0, 1, 2], harness.Delays);
    }

    [Fact]
    public async Task RunAsync_ReReadsTheBaseDelayEveryIteration()
    {
        var harness = new Harness(sweeps: [true, true, true]);
        var bases = new Queue<int>([30, 45, 60]);

        await harness.RunAsync(initialDelay: TimeSpan.Zero, baseIdleSeconds: () => bases.Dequeue(), maxIdleSeconds: 300);

        Assert.Equal([0, 30, 45, 60], harness.Delays);
    }

    [Fact]
    public async Task RunAsync_ReportsAFailedSweep_AndBacksOffAsIfIdle()
    {
        var boom = new InvalidOperationException("boom");
        var harness = new Harness(sweeps: [() => throw boom, () => Task.FromResult(false)]);

        await harness.RunAsync(initialDelay: TimeSpan.Zero, baseIdleSeconds: 30, maxIdleSeconds: 300);

        Assert.Equal([boom], harness.Failures);
        Assert.Equal([0, 30, 60], harness.Delays);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringTheInitialDelay_NeverSweeps()
    {
        using var cts = new CancellationTokenSource();
        var harness = new Harness(sweeps: [true]);
        cts.Cancel();

        await harness.RunAsync(initialDelay: TimeSpan.FromSeconds(10), baseIdleSeconds: 30, maxIdleSeconds: 300, cts.Token);

        Assert.Equal(0, harness.SweepCalls);
    }

    [Fact]
    public async Task RunAsync_CancelledInsideASweep_StopsWithoutReportingAFailure()
    {
        using var cts = new CancellationTokenSource();
        var harness = new Harness(sweeps:
        [
            () =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        ]);

        await harness.RunAsync(initialDelay: TimeSpan.Zero, baseIdleSeconds: 30, maxIdleSeconds: 300, cts.Token);

        Assert.Equal(1, harness.SweepCalls);
        Assert.Empty(harness.Failures);
        Assert.Equal([0], harness.Delays); // no idle wait was scheduled after the cancelled sweep
    }

    [Fact]
    public async Task RunAsync_AnOperationCanceledExceptionWithoutCancellation_IsAnOrdinaryFailure()
    {
        // A sweep whose own inner timeout throws OCE while the loop's token is live must not end the loop.
        var harness = new Harness(sweeps: [() => throw new OperationCanceledException(), () => Task.FromResult(true)]);

        await harness.RunAsync(initialDelay: TimeSpan.Zero, baseIdleSeconds: 30, maxIdleSeconds: 300);

        Assert.Single(harness.Failures);
        Assert.Equal(2, harness.SweepCalls);
    }

    /// <summary>
    /// Drives the loop with a scripted sweep sequence and a fake delay that records each requested
    /// wait and cancels the loop once the script is exhausted (the loop is otherwise endless).
    /// </summary>
    private sealed class Harness
    {
        private readonly Queue<Func<Task<bool>>> _sweeps;
        private readonly CancellationTokenSource _endOfScript = new();

        public Harness(IEnumerable<bool> sweeps)
            : this(sweeps.Select(found => (Func<Task<bool>>)(() => Task.FromResult(found)))) { }

        public Harness(IEnumerable<Func<Task<bool>>> sweeps) => _sweeps = new Queue<Func<Task<bool>>>(sweeps);

        public List<int> Delays { get; } = [];
        public List<Exception> Failures { get; } = [];
        public int SweepCalls { get; private set; }

        public Task RunAsync(TimeSpan initialDelay, int baseIdleSeconds, int maxIdleSeconds, CancellationToken ct = default) =>
            RunAsync(initialDelay, () => baseIdleSeconds, maxIdleSeconds, ct);

        public async Task RunAsync(TimeSpan initialDelay, Func<int> baseIdleSeconds, int maxIdleSeconds, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _endOfScript.Token);
            await IdleBackoffSweepLoop.RunAsync(
                initialDelay, baseIdleSeconds, maxIdleSeconds, Sweep, Failures.Add, linked.Token, Delay);
        }

        private async Task<bool> Sweep(CancellationToken ct)
        {
            SweepCalls++;
            return await _sweeps.Dequeue()();
        }

        private Task Delay(TimeSpan wait, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add((int)wait.TotalSeconds);
            if (_sweeps.Count == 0)
            {
                _endOfScript.Cancel();
                return Task.FromCanceled(_endOfScript.Token);
            }
            return Task.CompletedTask;
        }
    }
}
