using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

// Same contract as QualityGradingChannelTests: the album auto-sweep re-runs every IdleDelaySeconds and
// would otherwise re-enqueue albums still queued/in-flight. The channel dedupes by album id; a forced
// "grade now" still always runs.
public class AlbumGradingChannelTests
{
    [Fact]
    public void EnqueueRange_DropsDuplicatesAndAlreadyQueuedIds()
    {
        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());

        channel.EnqueueRange([5, 5, 6], force: false); // within-batch dup
        channel.EnqueueRange([5, 6, 7], force: false); // 5 and 6 already queued

        var enqueued = Drain(channel);

        Assert.Equal(new[] { 5, 6, 7 }, enqueued.OrderBy(x => x));
    }

    [Fact]
    public void MarkProcessed_ReleasesIdSoItCanBeEnqueuedAgain()
    {
        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());

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
        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());

        channel.Enqueue(5, force: false);
        channel.Enqueue(5, force: true); // manual "grade now" must run despite the queued sweep item

        var enqueued = Drain(channel);

        Assert.Equal(2, enqueued.Count);
        Assert.All(enqueued, id => Assert.Equal(5, id));
    }

    [Fact]
    public void ProgressTotal_CountsOnlyWhatWasQueued()
    {
        var tracker = new AlbumGradingProgressTracker();
        var channel = new AlbumGradingChannel(tracker);

        channel.EnqueueRange([1, 2], force: false);
        channel.EnqueueRange([1, 2, 3], force: false); // a second sweep: only 3 is new

        Assert.Equal(3, tracker.GetCurrent()!.Total);
    }

    private static List<int> Drain(AlbumGradingChannel channel)
    {
        var ids = new List<int>();
        while (channel.Reader.TryRead(out var item)) ids.Add(item.CanonicalAlbumId);
        return ids;
    }
}
