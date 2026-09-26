using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MusicHoarder.Api.Chat;

/// <summary>One event bound for a chat stream: its SSE <c>event:</c> name and payload.</summary>
public sealed record ChatStreamEvent(string Type, object Payload);

/// <summary>
/// The live half of chat: which streams each account has open, so a new message or a read reaches
/// every device at once. In memory, like <see cref="Playback.PlaybackCoordinator"/>, because the API
/// is one replica. Nothing here is state a client relies on: every event only says "go and look",
/// and a stream that (re)opens starts with <see cref="ChatEventTypes.Ready"/>, on which the client
/// re-reads — so a dropped event costs a moment's delay, never a message.
/// </summary>
public sealed class ChatHub
{
    /// <summary>Events a stream may fall behind by before the oldest are dropped.</summary>
    private const int Capacity = 64;

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ChatStreamEvent>>> _streams = new();

    public ChatHubSubscription Subscribe(Guid userId)
    {
        var channel = Channel.CreateBounded<ChatStreamEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _streams.GetOrAdd(userId, _ => new()).TryAdd(id, channel);
        return new ChatHubSubscription(channel.Reader, () => Unsubscribe(userId, id));
    }

    public void Publish(Guid userId, ChatStreamEvent evt)
    {
        if (!_streams.TryGetValue(userId, out var streams)) return;
        foreach (var channel in streams.Values)
            channel.Writer.TryWrite(evt);
    }

    /// <summary>Open streams for an account; for tests and diagnostics.</summary>
    public int StreamCount(Guid userId) => _streams.TryGetValue(userId, out var s) ? s.Count : 0;

    private void Unsubscribe(Guid userId, Guid id)
    {
        if (!_streams.TryGetValue(userId, out var streams)) return;
        // The account's (now possibly empty) map stays: removing it could race a stream opening on
        // it, and there is one small map per account that ever connected.
        if (streams.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }
}

public sealed class ChatHubSubscription(ChannelReader<ChatStreamEvent> reader, Action dispose) : IDisposable
{
    private int _disposed;

    public ChannelReader<ChatStreamEvent> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
    }
}
