using System.Collections.Concurrent;

namespace MusicHoarder.Api.Jobs;

/// <summary>
/// Remembers work items that just failed, with the UTC instant each becomes eligible again, so a
/// periodic sweep can skip them for a backoff window instead of re-enqueuing them every pass. A
/// failure typically persists nothing (no grade row, no attempt), which is exactly why the sweep
/// cannot tell a fresh item from one that failed a moment ago without this. In-memory only: a restart
/// retries everything, and a forced manual run bypasses the sweep entirely.
/// </summary>
public sealed class FailureBackoffTracker
{
    private readonly ConcurrentDictionary<int, DateTime> _failedUntil = new();

    /// <summary>Marks <paramref name="id"/> as recently failed: the sweep skips it until <paramref name="window"/> has passed.</summary>
    public void MarkFailed(int id, TimeSpan window) =>
        _failedUntil[id] = DateTime.UtcNow + window;

    /// <summary>Forgets a backoff, e.g. because the item has since succeeded.</summary>
    public void Clear(int id) => _failedUntil.TryRemove(id, out _);

    /// <summary>True while <paramref name="id"/> is inside its backoff window as of <paramref name="nowUtc"/>.</summary>
    public bool IsBackingOff(int id, DateTime nowUtc) =>
        _failedUntil.TryGetValue(id, out var until) && until > nowUtc;

    /// <summary>Drops every entry whose window has expired as of <paramref name="nowUtc"/>, so the set cannot grow without bound.</summary>
    public void Prune(DateTime nowUtc)
    {
        foreach (var kvp in _failedUntil)
            if (kvp.Value <= nowUtc)
                _failedUntil.TryRemove(kvp.Key, out _);
    }

    /// <summary>Number of ids currently remembered (expired entries included until the next <see cref="Prune"/>).</summary>
    public int Count => _failedUntil.Count;
}
