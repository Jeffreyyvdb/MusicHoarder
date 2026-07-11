using System.Threading.Channels;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Singleton work queue of <see cref="Persistence.UpgradeRequest"/> ids, feeding
/// <see cref="SoulseekUpgradeBackgroundService"/>. Independent of <c>JobManager</c> — upgrade
/// searches are network side-work that must not hold the pipeline's one-job lock (they only
/// TRIGGER Scan/Build jobs). One-active-request-per-song is enforced at the endpoint, so no
/// dedupe is needed here.
/// </summary>
public class SoulseekUpgradeChannel
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ChannelReader<int> Reader => _channel.Reader;

    public void Enqueue(int upgradeRequestId) => _channel.Writer.TryWrite(upgradeRequestId);
}
