using MusicHoarder.Api.Playback;

namespace MusicHoarder.Api.Tests.Playback;

/// <summary>
/// A stream's mailbox: full-state events may be dropped in favour of newer ones, commands never.
/// </summary>
public class PlaybackSubscriberTests
{
    private static PlaybackStreamEvent Session(long version) =>
        new(PlaybackEventTypes.Session, version);

    private static PlaybackStreamEvent Devices(string tag) =>
        new(PlaybackEventTypes.Devices, tag);

    private static PlaybackStreamEvent Command(string id) =>
        new(PlaybackEventTypes.Command, id);

    [Fact]
    public async Task A_newer_session_or_device_list_replaces_one_not_yet_written()
    {
        var subscriber = new PlaybackSubscriber("device-0001");
        subscriber.Enqueue(Session(1));
        subscriber.Enqueue(Devices("a"));
        subscriber.Enqueue(Session(2));
        subscriber.Enqueue(Devices("b"));
        subscriber.Enqueue(Session(3));

        var read = await ReadAll(subscriber);

        // Each newer one took the place of the one it replaced.
        Assert.Equal([Session(3), Devices("b")], read);
    }

    [Fact]
    public async Task A_newer_session_never_falls_behind_a_command_queued_after_the_one_it_replaces()
    {
        // A transfer must be run from state at least as new as when it was sent.
        var subscriber = new PlaybackSubscriber("device-0001");
        subscriber.Enqueue(Session(5));
        subscriber.Enqueue(Command("c1"));
        subscriber.Enqueue(Session(6));

        var read = await ReadAll(subscriber);

        Assert.Equal([Session(6), Command("c1")], read);
    }

    [Fact]
    public async Task Commands_are_never_dropped_and_keep_their_order()
    {
        var subscriber = new PlaybackSubscriber("device-0001");
        for (var i = 0; i < 10; i++)
        {
            subscriber.Enqueue(Command($"c{i}"));
            subscriber.Enqueue(Session(i));
        }

        var read = await ReadAll(subscriber);

        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"c{i}"),
            read.Where(e => e.Type == PlaybackEventTypes.Command).Select(e => (string)e.Payload));
        Assert.Equal(Session(9), Assert.Single(read, e => e.Type == PlaybackEventTypes.Session));
    }

    [Fact]
    public async Task A_stream_that_stops_draining_is_closed_rather_than_losing_a_command()
    {
        var subscriber = new PlaybackSubscriber("device-0001");
        for (var i = 0; i <= PlaybackSubscriber.MaxPendingCommands; i++)
            subscriber.Enqueue(Command($"c{i}"));

        Assert.True(subscriber.IsClosed);

        // What was queued is still written; then the stream ends, and the client reconnects.
        for (var i = 0; i < PlaybackSubscriber.MaxPendingCommands; i++)
            Assert.Equal(Command($"c{i}"), await subscriber.NextAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.Null(await subscriber.NextAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Nothing_to_send_becomes_a_ping()
    {
        var subscriber = new PlaybackSubscriber("device-0001");

        var next = await subscriber.NextAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.Same(PlaybackStreamEvent.Ping, next);
    }

    [Fact]
    public async Task A_waiting_reader_wakes_on_the_next_event()
    {
        var subscriber = new PlaybackSubscriber("device-0001");
        var waiting = subscriber.NextAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        subscriber.Enqueue(Command("c1"));

        Assert.Equal(Command("c1"), await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancelling_or_closing_ends_the_stream()
    {
        var subscriber = new PlaybackSubscriber("device-0001");
        using var cts = new CancellationTokenSource();
        var waiting = subscriber.NextAsync(TimeSpan.FromSeconds(30), cts.Token);

        cts.Cancel();
        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        subscriber.Close();
        subscriber.Enqueue(Session(1));
        Assert.Null(await subscriber.NextAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private static async Task<List<PlaybackStreamEvent>> ReadAll(PlaybackSubscriber subscriber)
    {
        var events = new List<PlaybackStreamEvent>();
        while (await subscriber.NextAsync(TimeSpan.Zero, CancellationToken.None) is { } evt
               && !ReferenceEquals(evt, PlaybackStreamEvent.Ping))
            events.Add(evt);
        return events;
    }
}
