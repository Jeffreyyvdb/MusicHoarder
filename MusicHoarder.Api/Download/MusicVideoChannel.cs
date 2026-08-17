using System.Threading.Channels;

namespace MusicHoarder.Api.Download;

public enum MusicVideoWorkKind
{
    /// <summary>Download (or re-download) the video, then align it against the song's audio.</summary>
    Fetch = 0,

    /// <summary>Only (re-)estimate the audio↔video sync offset for an already-downloaded video.</summary>
    Align = 1,
}

/// <summary>
/// One unit of music-video work. <paramref name="ExplicitUrl"/> pins a specific YouTube video for a
/// <see cref="MusicVideoWorkKind.Fetch"/>; null searches by the song's artist/title.
/// </summary>
public record MusicVideoWorkItem(int SongId, MusicVideoWorkKind Kind, string? ExplicitUrl = null);

/// <summary>
/// Singleton work queue feeding <see cref="MusicVideoBackgroundService"/>. Mirrors
/// <see cref="QualityUpgradeChannel"/>: video fetches/alignments are slow network/CPU side-work that
/// must not hold the pipeline's one-job lock. Duplicate enqueues are harmless — the worker upserts
/// the single per-song <see cref="Persistence.SongMusicVideo"/> row idempotently.
/// </summary>
public class MusicVideoChannel
{
    private readonly Channel<MusicVideoWorkItem> _channel = Channel.CreateUnbounded<MusicVideoWorkItem>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ChannelReader<MusicVideoWorkItem> Reader => _channel.Reader;

    public void Enqueue(MusicVideoWorkItem item) => _channel.Writer.TryWrite(item);
}
