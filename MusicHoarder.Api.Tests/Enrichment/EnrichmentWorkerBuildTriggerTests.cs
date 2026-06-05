using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Covers build chaining in <see cref="EnrichmentBackgroundService"/>: the worker does <em>not</em>
/// trigger a library build per matched song. A per-song trigger caused a build-run storm — a build
/// that finds no new work completes in milliseconds and flips the Build step back out of Running, so
/// the "no-op while running" guard almost never held and every match restarted a full run, churning
/// the DB. The build is instead kicked once per enrichment cycle when the channel drains (covered by
/// <see cref="EnrichmentPipelineChannelTests"/>); that single run's batch loop sweeps every matched
/// song. These tests keep a second item in flight to prove no build fires mid-cycle.
/// </summary>
public class EnrichmentWorkerBuildTriggerTests
{
    [Theory]
    [InlineData(EnrichmentOutcome.Matched)]
    [InlineData(EnrichmentOutcome.NeedsReview)]
    [InlineData(EnrichmentOutcome.Failed)]
    [InlineData(EnrichmentOutcome.Skipped)]
    public async Task Worker_DoesNotTriggerBuild_MidCycle(EnrichmentOutcome outcome)
    {
        var jobs = new JobManager();
        var channel = new EnrichmentPipelineChannel(jobs, new EnrichmentProgressTracker());
        var orchestrator = new GatedOrchestrator(outcome);
        await using var harness = StartWorker(jobs, channel, orchestrator);

        // Two items, concurrency 1: the first is processed, the second blocks on the gate — so the
        // enrich cycle is still in flight (its drain trigger cannot have fired yet).
        channel.EnqueueRange([1, 2]);
        await orchestrator.SecondStarted; // first song fully processed; cycle still in flight

        Assert.Equal("Running", jobs.GetStepSnapshot(JobType.Enrich).Status); // cycle not drained
        Assert.Equal("Idle", jobs.GetStepSnapshot(JobType.Build).Status);     // no per-song build
        Assert.False(jobs.BuildTriggers.TryRead(out _));

        orchestrator.Release();
    }

    private static WorkerHarness StartWorker(JobManager jobs, EnrichmentPipelineChannel channel, IEnrichmentOrchestrator orchestrator)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            AutoStartPipeline = false, // manual/preview mode: only the always-on workers run
            EnrichmentWorkerConcurrency = 1,
        });

        var service = new EnrichmentBackgroundService(
            new NullScopeFactory(),
            jobs,
            new EnrichmentProgressTracker(),
            channel,
            orchestrator,
            opts,
            NullLogger<EnrichmentBackgroundService>.Instance);

        var cts = new CancellationTokenSource();
        service.StartAsync(cts.Token);
        return new WorkerHarness(service, cts);
    }

    private sealed class WorkerHarness(EnrichmentBackgroundService service, CancellationTokenSource cts) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try { await service.StopAsync(CancellationToken.None); } catch { /* shutting down */ }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Returns <paramref name="outcome"/> for the first song immediately, then blocks every later
    /// song on a gate so the enrich cycle stays in flight. <see cref="SecondStarted"/> completes once
    /// the second song's processing begins — i.e. the first song (and its build trigger) is done.
    /// </summary>
    private sealed class GatedOrchestrator(EnrichmentOutcome outcome) : IEnrichmentOrchestrator
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task SecondStarted => _secondStarted.Task;
        public void Release() => _gate.TrySetResult();

        public async Task<EnrichmentOutcome> ProcessSongAsync(int songId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _count) >= 2)
            {
                _secondStarted.TrySetResult();
                await _gate.Task.WaitAsync(ct);
            }
            return outcome;
        }

        public Task<IReadOnlySet<EnrichmentProvider>> GetEnabledProviderEnumsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<EnrichmentProvider>>(new HashSet<EnrichmentProvider>());
    }

    // The Matched path never opens a DB scope, so a no-op factory is enough for the worker loop.
    private sealed class NullScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NullScope();

        private sealed class NullScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new NullProvider();
            public void Dispose() { }

            private sealed class NullProvider : IServiceProvider
            {
                public object? GetService(Type serviceType) => null;
            }
        }
    }
}
