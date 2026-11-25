using System.Collections.Concurrent;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.BackgroundWork;

/// <summary>What a stored candidate list came to: the schedule's outcome, and the facts the charts read off it.</summary>
/// <param name="TopScore">The best candidate's score; null for an empty list.</param>
/// <param name="PickBytes">The size of the clip <see cref="MusicVideoAutoPick"/> would download; null without one.</param>
public sealed record MusicVideoVerdict(MusicVideoSearchOutcome Outcome, int? TopScore, long? PickBytes);

/// <summary>
/// Remembers each stored set's verdict, so the sheet reads a list's JSON once per search rather than once per
/// refresh. A set is a new search when its <c>SearchedAtUtc</c> moves, and the verdict also depends on the
/// options' score and size gates, so those are part of the key. A probe the picker writes into a list leaves
/// <c>SearchedAtUtc</c> alone, as it leaves the schedule's decision alone.
/// </summary>
public sealed class MusicVideoOutcomeMemo
{
    private sealed record Entry(DateTime SearchedAtUtc, string Gates, MusicVideoVerdict Verdict);

    private readonly ConcurrentDictionary<int, Entry> _bySet = new();

    public static string GatesOf(MusicEnricherOptions options)
        => $"{options.MusicVideoAutoDownloadMinScore}/{options.MusicVideoAutoDownloadMaxClipMb}";

    public MusicVideoVerdict? Get(int setId, DateTime searchedAtUtc, string gates)
        => _bySet.TryGetValue(setId, out var entry) && entry.SearchedAtUtc == searchedAtUtc && entry.Gates == gates
            ? entry.Verdict
            : null;

    public void Remember(int setId, DateTime searchedAtUtc, string gates, MusicVideoVerdict verdict)
        => _bySet[setId] = new Entry(searchedAtUtc, gates, verdict);

    /// <summary>Forgets the sets that no longer exist.</summary>
    public void Retain(IReadOnlySet<int> setIds)
    {
        foreach (var id in _bySet.Keys)
        {
            if (!setIds.Contains(id))
                _bySet.TryRemove(id, out _);
        }
    }
}
