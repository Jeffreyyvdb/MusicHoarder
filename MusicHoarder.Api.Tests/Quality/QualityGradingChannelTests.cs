using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

// The auto-sweep re-runs every IdleDelaySeconds and would otherwise re-enqueue the same ungraded
// songs that are still queued/in-flight, flooding the channel (and the grading API) with duplicates.
// The channel dedupes by song id; a forced "grade now" still always runs.
public class QualityGradingChannelTests
{
    [Fact]
    public void EnqueueRange_DropsDuplicatesAndAlreadyQueuedIds()
    {
        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());

        channel.EnqueueRange([5, 5, 6], force: false); // within-batch dup
        channel.EnqueueRange([5, 6, 7], force: false); // 5 and 6 already queued

        var enqueued = Drain(channel);

        Assert.Equal(new[] { 5, 6, 7 }, enqueued.OrderBy(x => x));
    }

    [Fact]
    public void MarkProcessed_ReleasesIdSoItCanBeEnqueuedAgain()
    {
        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());

        channel.Enqueue(5, force: false);
        Assert.True(channel.Reader.TryRead(out _));
        channel.MarkProcessed(5);

        channel.Enqueue(5, force: false); // no longer queued/in-flight, so it goes through
        var enqueued = Drain(channel);

        Assert.Equal(new[] { 5 }, enqueued);
    }

    [Fact]
    public void Force_AlwaysEnqueues_EvenWhenAlreadyQueued()
    {
        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());

        channel.Enqueue(5, force: false);
        channel.Enqueue(5, force: true); // manual "grade now" must run despite the queued sweep item

        var enqueued = Drain(channel);

        Assert.Equal(2, enqueued.Count);
        Assert.All(enqueued, id => Assert.Equal(5, id));
    }

    private static List<int> Drain(QualityGradingChannel channel)
    {
        var ids = new List<int>();
        while (channel.Reader.TryRead(out var item)) ids.Add(item.SongId);
        return ids;
    }
}
