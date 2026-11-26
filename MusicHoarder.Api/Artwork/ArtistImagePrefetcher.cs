using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// The queue between the HTTP request path and the background portrait fetcher. Request handlers
/// only ever <see cref="Enqueue"/> a name here — never wait on a provider — and
/// <see cref="ArtistImagePrefetchBackgroundService"/> drains the queue at the providers' pace.
/// Deduplicated: a name already queued or being fetched is not queued twice, and it can be
/// re-enqueued once its fetch finished (so a later staleness re-triggers it).
/// </summary>
public interface IArtistImagePrefetcher
{
    /// <summary>Queue a display name for background refresh. Drops silently when the queue is full.</summary>
    void Enqueue(string name);

    /// <summary>Read the next queued name, waiting up to <paramref name="timeout"/> when empty.</summary>
    Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Called by the worker when a name's fetch pass finished, so it can be queued again later.</summary>
    void Release(string name);

    /// <summary>Names queued or in flight right now (diagnostics).</summary>
    int PendingCount { get; }
}

public sealed class ArtistImagePrefetcher : IArtistImagePrefetcher
{
    private const int Capacity = 100_000;

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    public void Enqueue(string name)
    {
        var normalized = Matching.TitleNormalizer.NormalizeForDedup(name);
        if (normalized.Length == 0) return;
        if (!_inFlight.TryAdd(normalized, 0)) return;
        if (!_queue.Writer.TryWrite(name))
            _inFlight.TryRemove(normalized, out _);
    }

    public async Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var name = await _queue.Reader.ReadAsync(timeoutCts.Token);
            // The name stays "in flight" until the worker says it is done (see Release), so a
            // burst of requests for one artist queues it once.
            return name;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Release(string name)
    {
        var normalized = Matching.TitleNormalizer.NormalizeForDedup(name);
        _inFlight.TryRemove(normalized, out _);
    }

    public int PendingCount => _inFlight.Count;
}
