using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Sync;

/// <summary>
/// Singleton work queue of song ids to push to the remote instance, feeding
/// <see cref="TrackSyncBackgroundService"/>. Deliberately independent of <c>JobManager</c>: sync is
/// a long-tailed network side-effect that must never starve (or be starved by) a Build run, and
/// per-track consumption doesn't fit the one-job-at-a-time model. Dedupes ids already queued or in
/// flight so the periodic sweep can re-enqueue broadly without flooding.
/// </summary>
public class TrackSyncChannel
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly object _lock = new();
    private readonly HashSet<int> _queued = [];

    public ChannelReader<int> Reader => _channel.Reader;

    public void Enqueue(int songId) => EnqueueRange([songId]);

    public void EnqueueRange(IEnumerable<int> songIds)
    {
        List<int> toQueue;
        lock (_lock)
        {
            toQueue = songIds.Where(_queued.Add).ToList();
        }
        foreach (var id in toQueue)
            _channel.Writer.TryWrite(id);
    }

    /// <summary>Called once per dequeued id, whatever the outcome, so it can be re-enqueued later.</summary>
    public void MarkProcessed(int songId)
    {
        lock (_lock)
        {
            _queued.Remove(songId);
        }
    }
}

/// <summary>
/// Thin gate the library builder calls after a track's build commits. A no-op unless this instance
/// is configured to push, so <c>LibraryBuilderService</c> needs no sync knowledge beyond one call —
/// and demo-tenant builds never reach the channel.
/// </summary>
public interface ITrackSyncEnqueuer
{
    void TryEnqueue(int songId, Guid ownerUserId);
}

public sealed class TrackSyncEnqueuer(
    TrackSyncChannel channel,
    IOptionsMonitor<SyncOptions> options) : ITrackSyncEnqueuer
{
    public void TryEnqueue(int songId, Guid ownerUserId)
    {
        if (!options.CurrentValue.IsPushConfigured)
            return;
        if (ownerUserId == WellKnownUsers.DemoId)
            return;
        channel.Enqueue(songId);
    }
}
