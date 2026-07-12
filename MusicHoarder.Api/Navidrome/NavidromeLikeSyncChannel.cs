using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Navidrome;

/// <summary>
/// Singleton queue of song ids to push to Navidrome immediately after a like toggles, so the star
/// shows up without waiting for the periodic sweep. Dedupes ids already queued/in-flight. The
/// periodic <see cref="NavidromeLikeSyncBackgroundService"/> sweep is the backstop and owns the
/// Navidrome → MH direction.
/// </summary>
public sealed class NavidromeLikeSyncChannel
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly object _lock = new();
    private readonly HashSet<int> _queued = [];

    public ChannelReader<int> Reader => _channel.Reader;

    public void Enqueue(int songId)
    {
        lock (_lock)
        {
            if (!_queued.Add(songId)) return;
        }
        _channel.Writer.TryWrite(songId);
    }

    public void MarkProcessed(int songId)
    {
        lock (_lock) { _queued.Remove(songId); }
    }
}

/// <summary>
/// Thin gate the like endpoint calls after a toggle commits. A no-op unless Navidrome is configured
/// and the song belongs to the owner tenant (demo likes never leave the instance).
/// </summary>
public interface INavidromeLikeEnqueuer
{
    void TryEnqueue(int songId, Guid ownerUserId);
}

public sealed class NavidromeLikeEnqueuer(
    NavidromeLikeSyncChannel channel,
    IOptionsMonitor<NavidromeOptions> options) : INavidromeLikeEnqueuer
{
    public void TryEnqueue(int songId, Guid ownerUserId)
    {
        if (!options.CurrentValue.IsConfigured) return;
        if (ownerUserId == WellKnownUsers.DemoId) return;
        channel.Enqueue(songId);
    }
}
