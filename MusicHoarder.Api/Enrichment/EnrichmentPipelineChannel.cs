using System.Threading.Channels;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Singleton channel that receives SongIds ready for enrichment.
/// Fed by fingerprint completion, startup backfill, and the periodic retry sweep.
/// </summary>
public class EnrichmentPipelineChannel
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ChannelWriter<int> Writer => _channel.Writer;
    public ChannelReader<int> Reader => _channel.Reader;

    public void Enqueue(int songId) => _channel.Writer.TryWrite(songId);

    public void EnqueueRange(IEnumerable<int> songIds)
    {
        foreach (var id in songIds)
            _channel.Writer.TryWrite(id);
    }
}
