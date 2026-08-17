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
}
