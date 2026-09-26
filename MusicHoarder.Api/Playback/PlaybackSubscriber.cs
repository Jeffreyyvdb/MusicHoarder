namespace MusicHoarder.Api.Playback;

/// <summary>One event bound for a stream: its SSE <c>event:</c> name and its payload.</summary>
public sealed record PlaybackStreamEvent(string Type, object Payload)
{
    public static readonly PlaybackStreamEvent Ping = new(PlaybackEventTypes.Ping, new PlaybackPingEvent());
}

/// <summary>
/// The mailbox of one open stream. Bounded, but not by dropping blindly: a <c>session</c> or
/// <c>devices</c> event carries the full state, so a newer one simply takes the place of an older
/// one that has not been written yet, while a <c>command</c> is never dropped. A stream that stops draining
/// until <see cref="MaxPendingCommands"/> commands pile up is closed instead, so its client
/// reconnects and starts again from a snapshot rather than silently missing a command.
/// </summary>
internal sealed class PlaybackSubscriber
{
    internal const int MaxPendingCommands = 64;

    private readonly object _gate = new();
    private readonly LinkedList<PlaybackStreamEvent> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _signaled;
    private bool _closed;
    private int _pendingCommands;

    public PlaybackSubscriber(string deviceId)
    {
        DeviceId = deviceId;
    }

    public string DeviceId { get; }

    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    public void Enqueue(PlaybackStreamEvent evt)
    {
        lock (_gate)
        {
            if (_closed) return;

            if (evt.Type == PlaybackEventTypes.Command)
            {
                if (_pendingCommands >= MaxPendingCommands)
                {
                    CloseLocked();
                    return;
                }
                _pendingCommands++;
            }
            else
            {
                // Full-state events coalesce: the newest supersedes one still waiting, in its
                // place, so it never falls behind a command queued after the state it replaces (a
                // transfer must be run from state at least as new as when it was sent).
                for (var node = _pending.First; node is not null; node = node.Next)
                {
                    if (node.Value.Type == evt.Type)
                    {
                        node.Value = evt;
                        return;
                    }
                }
            }

            _pending.AddLast(evt);
            SignalLocked();
        }
    }

    public void Close()
    {
        lock (_gate) CloseLocked();
    }

    /// <summary>
    /// The next event, <see cref="PlaybackStreamEvent.Ping"/> when nothing arrived within
    /// <paramref name="pingAfter"/>, or null once the stream should end (closed or cancelled).
    /// </summary>
    public async Task<PlaybackStreamEvent?> NextAsync(TimeSpan pingAfter, CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_pending.First is { } first)
                {
                    _pending.RemoveFirst();
                    if (first.Value.Type == PlaybackEventTypes.Command) _pendingCommands--;
                    return first.Value;
                }
                if (_closed) return null;
                _signaled = false;
            }

            bool woken;
            try
            {
                woken = await _signal.WaitAsync(pingAfter, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (!woken) return PlaybackStreamEvent.Ping;
        }
    }

    private void CloseLocked()
    {
        if (_closed) return;
        _closed = true;
        SignalLocked();
    }

    private void SignalLocked()
    {
        // One wake-up per empty→non-empty transition; the reader re-checks the queue after every
        // wake, so a leftover permit costs one extra loop, never a missed event.
        if (_signaled) return;
        _signaled = true;
        _signal.Release();
    }
}
