using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Covers the per-song build chaining in <see cref="EnrichmentBackgroundService"/>: a song that the
/// orchestrator resolves to <see cref="EnrichmentOutcome.Matched"/> triggers a library build
/// immediately — whatever enqueued it and even with AutoStartPipeline off (the preview/manual mode,
/// where the builder never auto-polls). This fires <em>before</em> the enrich cycle drains, which is
/// what makes a big bulk enrich start landing tracks in the library as they match instead of all at
/// the very end. (The channel's separate cycle-drain trigger already builds once at the end of every
/// cycle, so these tests keep a second item in flight to isolate the per-song behavior.)
/// </summary>
public class EnrichmentWorkerBuildTriggerTests
{
    [Fact]
    public async Task Worker_TriggersBuild_AssoonAsASongMatches_BeforeCycleDrains()
    {
        var jobs = new JobManager();
        var channel = new EnrichmentPipelineChannel(jobs, new EnrichmentProgressTracker());
        var orchestrator = new GatedOrchestrator(EnrichmentOutcome.Matched);
        await using var harness = StartWorker(jobs, channel, orchestrator);

        // Two items, concurrency 1: the first is processed, the second blocks on the gate — so the
        // enrich cycle is still in flight (its drain trigger cannot have fired yet).
        channel.EnqueueRange([1, 2]);
        await orchestrator.SecondStarted;

        Assert.Equal("Running", jobs.GetStepSnapshot(JobType.Enrich).Status); // cycle not drained
        Assert.Equal("Running", jobs.GetStepSnapshot(JobType.Build).Status);  // matched song built it
        Assert.True(jobs.BuildTriggers.TryRead(out var buildJobId));
        Assert.NotEqual(Guid.Empty, buildJobId);

        orchestrator.Release();
    }

    [Theory]
    [InlineData(EnrichmentOutcome.NeedsReview)]
    [InlineData(EnrichmentOutcome.Failed)]
    [InlineData(EnrichmentOutcome.Skipped)]
    public async Task Worker_DoesNotTriggerBuild_WhenSongDoesNotMatch(EnrichmentOutcome outcome)
    {
        var jobs = new JobManager();
        var channel = new EnrichmentPipelineChannel(jobs, new EnrichmentProgressTracker());
        var orchestrator = new GatedOrchestrator(outcome);
        await using var harness = StartWorker(jobs, channel, orchestrator);

        channel.EnqueueRange([1, 2]);
        await orchestrator.SecondStarted; // first song fully processed; cycle still in flight

        Assert.Equal("Idle", jobs.GetStepSnapshot(JobType.Build).Status);
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
