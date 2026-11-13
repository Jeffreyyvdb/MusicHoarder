using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Enrichment;

public class EnrichmentPipelineChannelPriorityTests
{
    private static EnrichmentPipelineChannel NewChannel() =>
        new(new JobManager(), new EnrichmentProgressTracker());

    private static async Task<List<int>> TakeAsync(EnrichmentPipelineChannel channel, int count)
    {
        var taken = new List<int>(count);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var id in channel.ReadAllPrioritizedAsync(cts.Token))
        {
            taken.Add(id);
            if (taken.Count == count) break;
        }
        return taken;
    }

    [Fact]
    public async Task PriorityItems_AreDequeuedBeforeTheBacklog()
    {
        var channel = NewChannel();
        channel.EnqueueRange([1, 2, 3]);      // the big backfill sweep
        channel.EnqueueRangePriority([99]);   // a fresh import, enqueued AFTER the backlog

        Assert.Equal([99, 1, 2, 3], await TakeAsync(channel, 4));
    }

    [Fact]
    public async Task PriorityItems_KeepTheirOwnFifoOrder()
    {
        var channel = NewChannel();
        channel.EnqueueRangePriority([10, 11]);
        channel.EnqueueRange([1]);
        channel.EnqueueRangePriority([12]);

        Assert.Equal([10, 11, 12, 1], await TakeAsync(channel, 4));
    }

    [Fact]
    public async Task MidStreamPriorityEnqueue_JumpsAheadOfRemainingBacklog()
    {
        var channel = NewChannel();
        channel.EnqueueRange([1, 2, 3]);

        var taken = new List<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var id in channel.ReadAllPrioritizedAsync(cts.Token))
        {
            taken.Add(id);
            if (taken.Count == 1)
                channel.EnqueueRangePriority([99]); // import lands while the backlog drains
            if (taken.Count == 4) break;
        }

        Assert.Equal([1, 99, 2, 3], taken);
    }

    [Fact]
    public void PriorityEnqueue_CountsTowardTheActiveCycle()
    {
        var channel = NewChannel();
        channel.EnqueueRangePriority([1, 2]);
        Assert.Equal(2, channel.InFlight);
        channel.MarkProcessed();
        channel.MarkProcessed();
        Assert.Equal(0, channel.InFlight);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/data/downloads", "/data/downloads/")]
    [InlineData("/data/downloads/", "/data/downloads/")]
    [InlineData("C:\\downloads\\", "C:/downloads/")]
    public void DownloadRootPrefix_NormalizesToTrailingSlashPrefix(string configured, string? expected)
    {
        var opts = new MusicEnricherOptions { DownloadDirectory = configured };
        Assert.Equal(expected, FingerprintBackgroundService.DownloadRootPrefix(opts));
    }

    // ── deduplication (issue #550) ──────────────────────────────────────────────────────

    [Fact]
    public async Task OverlappingSweeps_QueueEachSongOnce()
    {
        var (channel, tracker) = NewWithTracker();

        Assert.Equal(3, channel.EnqueueRange([1, 2, 3]));  // new-provider backfill
        Assert.Equal(1, channel.EnqueueRange([1, 2, 3, 4])); // pending backfill
        Assert.Equal(0, channel.EnqueueRange([2]));          // retry sweep

        Assert.Equal(4, channel.InFlight);
        Assert.Equal(4, channel.Waiting);
        Assert.Equal(4, tracker.GetCurrent()!.TotalTracks);
        Assert.Equal([1, 2, 3, 4], await TakeAsync(channel, 4));
        Assert.Equal(0, channel.Waiting);
    }

    [Fact]
    public async Task ASongAlreadyHandedToAWorker_CanBeQueuedAgain()
    {
        var channel = NewChannel();
        channel.EnqueueRange([1]);
        Assert.Equal([1], await TakeAsync(channel, 1));

        // e.g. a reset while the worker still holds the old row
        Assert.True(channel.Enqueue(1));
        Assert.Equal(2, channel.InFlight);
        Assert.Equal([1], await TakeAsync(channel, 1));
    }

    [Fact]
    public async Task PriorityEnqueue_PromotesAWaitingSong_WithoutCountingItTwice()
    {
        var channel = NewChannel();
        channel.EnqueueRange([1, 2, 3]);

        Assert.Equal(1, channel.EnqueueRangePriority([2]));
        Assert.Equal(3, channel.InFlight);

        // 2 jumps the queue, and its stale normal-lane entry is dropped rather than run again.
        Assert.Equal([2, 1, 3], await TakeAsync(channel, 3));
        Assert.Equal(0, channel.Waiting);
        Assert.Equal(0, channel.Reader.Count);
    }

    [Fact]
    public void NormalEnqueue_OfASongWaitingInPriority_IsIgnored()
    {
        var channel = NewChannel();
        channel.EnqueueRangePriority([7]);

        Assert.Equal(0, channel.EnqueueRange([7]));
        Assert.Equal(1, channel.InFlight);
        Assert.Equal(0, channel.Reader.Count);
    }

    [Fact]
    public async Task ResetCycle_ForgetsWaitingSongs_SoTheyCanBeQueuedAgain()
    {
        var channel = NewChannel();
        channel.EnqueueRange([1, 2]);
        channel.ResetCycle(cancelled: true);

        Assert.Equal(0, channel.Waiting);
        Assert.Equal(1, channel.EnqueueRange([1]));
        Assert.Equal([1], await TakeAsync(channel, 1));
    }

    private static (EnrichmentPipelineChannel, EnrichmentProgressTracker) NewWithTracker()
    {
        var tracker = new EnrichmentProgressTracker();
        return (new EnrichmentPipelineChannel(new JobManager(), tracker), tracker);
    }
}
