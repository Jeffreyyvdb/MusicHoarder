using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;

namespace MusicHoarder.Api.Tests.Enrichment;

public class EnrichmentPipelineChannelTests
{
    private static (EnrichmentPipelineChannel channel, JobManager jobs, EnrichmentProgressTracker tracker) New()
    {
        var jobs = new JobManager();
        var tracker = new EnrichmentProgressTracker();
        return (new EnrichmentPipelineChannel(jobs, tracker), jobs, tracker);
    }

    private static string EnrichStatus(JobManager jobs) => jobs.GetStepSnapshot(JobType.Enrich).Status;

    [Fact]
    public void Enqueue_starts_the_enrich_cycle()
    {
        var (channel, jobs, tracker) = New();

        channel.EnqueueRange([1, 2, 3], label: "Manual enrich — Kanye West");

        Assert.Equal("Running", EnrichStatus(jobs));
        Assert.Equal("Manual enrich — Kanye West", channel.CurrentLabel);
        var state = tracker.GetCurrent();
        Assert.NotNull(state);
        Assert.Equal(3, state!.TotalTracks);
        Assert.False(state.IsComplete);
    }

    [Fact]
    public void Cycle_completes_when_all_items_are_processed()
    {
        var (channel, jobs, tracker) = New();
        channel.EnqueueRange([1, 2], label: "Manual enrich — X");

        channel.MarkProcessed();
        Assert.Equal("Running", EnrichStatus(jobs)); // one still in flight

        channel.MarkProcessed();

        Assert.Equal("Completed", EnrichStatus(jobs));
        Assert.True(tracker.GetCurrent()!.IsComplete);
        Assert.Null(channel.CurrentLabel);
    }

    [Fact]
    public void Enqueue_while_running_grows_the_total_and_keeps_the_label()
    {
        var (channel, _, tracker) = New();
        channel.EnqueueRange([1, 2], label: "Manual enrich — X");

        channel.EnqueueRange([3], label: "Manual enrich — Y");

        Assert.Equal(3, tracker.GetCurrent()!.TotalTracks);
        Assert.Equal("Manual enrich — X", channel.CurrentLabel); // first label wins
    }

    [Fact]
    public void ResetCycle_cancels_and_clears_state()
    {
        var (channel, jobs, _) = New();
        channel.EnqueueRange([1, 2], label: "Manual enrich — X");

        channel.ResetCycle(cancelled: true);

        Assert.Equal("Cancelled", EnrichStatus(jobs));
        Assert.Null(channel.CurrentLabel);

        // Drained: nothing left to read.
        Assert.False(channel.Reader.TryRead(out _));

        // A subsequent enqueue starts a fresh cycle.
        channel.EnqueueRange([5], label: "Manual enrich — Z");
        Assert.Equal("Running", EnrichStatus(jobs));
        Assert.Equal("Manual enrich — Z", channel.CurrentLabel);
    }

    [Fact]
    public void Empty_enqueue_does_not_start_a_cycle()
    {
        var (channel, jobs, tracker) = New();

        channel.EnqueueRange([]);

        Assert.Equal("Idle", EnrichStatus(jobs));
        Assert.Null(tracker.GetCurrent());
    }

    [Fact]
    public void Completing_a_cycle_triggers_a_build()
    {
        var (channel, jobs, _) = New();
        channel.EnqueueRange([1, 2], label: "Manual enrich — X");

        channel.MarkProcessed();
        Assert.Equal("Idle", jobs.GetStepSnapshot(JobType.Build).Status); // not yet — one still in flight

        channel.MarkProcessed();

        // The chained build trigger: Build step is now Running and a job id was written.
        Assert.Equal("Running", jobs.GetStepSnapshot(JobType.Build).Status);
        Assert.True(jobs.BuildTriggers.TryRead(out var buildJobId));
        Assert.NotEqual(Guid.Empty, buildJobId);
    }

    [Fact]
    public void Cancelled_cycle_does_not_trigger_a_build()
    {
        var (channel, jobs, _) = New();
        channel.EnqueueRange([1, 2], label: "Manual enrich — X");

        channel.ResetCycle(cancelled: true);

        Assert.Equal("Idle", jobs.GetStepSnapshot(JobType.Build).Status);
        Assert.False(jobs.BuildTriggers.TryRead(out _));
    }
}
