using System.Collections.Concurrent;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.BackgroundWork;

public sealed record MusicVideoVerdict(MusicVideoSearchOutcome Outcome, int? TopScore, long? PickBytes);

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

    public void Retain(IReadOnlySet<int> setIds)
    {
        foreach (var id in _bySet.Keys)
        {
            if (!setIds.Contains(id))
                _bySet.TryRemove(id, out _);
        }
    }
}
