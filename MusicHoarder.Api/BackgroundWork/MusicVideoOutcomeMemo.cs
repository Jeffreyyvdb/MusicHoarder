using System.Collections.Concurrent;
using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.BackgroundWork;

public sealed record MusicVideoVerdict(MusicVideoSearchOutcome Outcome, int? TopScore, long? PickBytes);

/// <summary>Everything a verdict is computed from besides the stored list itself.</summary>
public sealed record MusicVideoVerdictKey(
    DateTime SearchedAtUtc, MusicVideoPickGates Gates, string? Artist, int? DurationMs, int? DurationSeconds);

/// <summary>
/// Each song's verdict on its stored candidate list, so the Background work page does not parse every list
/// on every open. The list's JSON can change without anything in the key changing (a probe measures a
/// candidate), so <see cref="MusicVideoCandidateCache"/> calls <see cref="Forget"/> after each rewrite.
/// </summary>
public sealed class MusicVideoOutcomeMemo
{
    private sealed record Entry(int Rewrites, MusicVideoVerdictKey Key, MusicVideoVerdict Verdict);

    private readonly ConcurrentDictionary<int, Entry> _bySong = new();
    private readonly ConcurrentDictionary<int, int> _rewrites = new();

    /// <summary>
    /// Read before the list's JSON and handed to <see cref="Remember"/>: a verdict computed from JSON that was
    /// rewritten meanwhile is then never served.
    /// </summary>
    public int RewritesOf(int songId) => _rewrites.GetValueOrDefault(songId);

    public MusicVideoVerdict? Get(int songId, MusicVideoVerdictKey key)
        => _bySong.TryGetValue(songId, out var entry) && entry.Key == key && entry.Rewrites == RewritesOf(songId)
            ? entry.Verdict
            : null;

    public void Remember(int songId, int rewritesSeen, MusicVideoVerdictKey key, MusicVideoVerdict verdict)
        => _bySong[songId] = new Entry(rewritesSeen, key, verdict);

    public void Forget(int songId)
    {
        _rewrites.AddOrUpdate(songId, 1, (_, n) => n + 1);
        _bySong.TryRemove(songId, out _);
    }

    public void Retain(IReadOnlySet<int> songIds)
    {
        foreach (var id in _bySong.Keys)
        {
            if (!songIds.Contains(id))
                _bySong.TryRemove(id, out _);
        }
        foreach (var id in _rewrites.Keys)
        {
            if (!songIds.Contains(id))
                _rewrites.TryRemove(id, out _);
        }
    }
}
