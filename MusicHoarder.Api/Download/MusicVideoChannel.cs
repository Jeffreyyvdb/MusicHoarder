using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MusicHoarder.Api.Download;

public enum MusicVideoWorkKind
{
    /// <summary>Download (or re-download) the video, then align it against the song's audio.</summary>
    Fetch = 0,

    /// <summary>Only (re-)estimate the audio↔video sync offset for an already-downloaded video.</summary>
    Align = 1,

    /// <summary>Only measure the black bars baked into an already-downloaded video that has not been measured.</summary>
    Measure = 2,
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
    private readonly ConcurrentDictionary<int, long> _reservedBytes = new();
    private readonly ConcurrentDictionary<int, byte> _activeFetches = new();

    public ChannelReader<MusicVideoWorkItem> Reader => _channel.Reader;

    public void Enqueue(MusicVideoWorkItem item) => _channel.Writer.TryWrite(item);

    /// <summary>
    /// Marks <paramref name="songId"/> as actively being downloaded by the background worker, so its
    /// disk reservation stays protected even if the song is soft-deleted mid-download.
    /// </summary>
    public void BeginFetch(int songId) => _activeFetches[songId] = 0;

    /// <summary>Marks <paramref name="songId"/>'s active download finished and releases its byte reservation.</summary>
    public void EndFetch(int songId)
    {
        _activeFetches.TryRemove(songId, out _);
        _reservedBytes.TryRemove(songId, out _);
    }

    /// <summary>
    /// Records the estimated size of an in-flight automatic download so later prefetch passes do not
    /// spend the same disk-space headroom twice before the file lands on disk.
    /// </summary>
    public void ReserveBytes(int songId, long bytes)
    {
        if (bytes > 0)
            _reservedBytes[songId] = bytes;
    }

    /// <summary>Releases a song's reservation once its fetch finishes, fails, or is skipped.</summary>
    public void ReleaseBytes(int songId) => _reservedBytes.TryRemove(songId, out _);

    /// <summary>
    /// Drops reservations for songs neither <see cref="Persistence.MusicVideoStatus.Fetching"/> nor
    /// currently active in the download worker.
    /// </summary>
    public void RetainReservations(IReadOnlySet<int> fetchingSongIds)
    {
        foreach (var id in _reservedBytes.Keys)
        {
            if (!fetchingSongIds.Contains(id) && !_activeFetches.ContainsKey(id))
                _reservedBytes.TryRemove(id, out _);
        }
    }

    public bool HasReservation(int songId) => _reservedBytes.ContainsKey(songId);

    public long ReservedBytes => _reservedBytes.Values.Sum();
}
