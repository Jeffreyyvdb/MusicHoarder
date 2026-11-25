namespace MusicHoarder.Api.BackgroundWork.Charts;

public enum MapState { Done, Waiting, Excluded }

public readonly record struct MapItem(DateTime Date, MapState State);

public static class LibraryMapBinner
{
    public const int MaxBins = 48;

    public static IReadOnlyList<LibraryMapBinDto> Bin(IReadOnlyList<MapItem> ordered, int maxBins = MaxBins)
    {
        var n = ordered.Count;
        if (n == 0)
            return [];

        var bins = Math.Min(n, maxBins);
        var result = new List<LibraryMapBinDto>(bins);
        for (var b = 0; b < bins; b++)
        {
            var from = (int)((long)b * n / bins);
            var to = (int)((long)(b + 1) * n / bins);
            DateTime min = DateTime.MaxValue, max = DateTime.MinValue;
            int done = 0, waiting = 0, excluded = 0;
            for (var i = from; i < to; i++)
            {
                var item = ordered[i];
                if (item.Date < min) min = item.Date;
                if (item.Date > max) max = item.Date;
                switch (item.State)
                {
                    case MapState.Done: done++; break;
                    case MapState.Waiting: waiting++; break;
                    default: excluded++; break;
                }
            }
            result.Add(new LibraryMapBinDto(min, max, done, waiting, excluded));
        }
        return result;
    }

    public static (DateTime Oldest, DateTime Newest)? WaitingFrontier(IReadOnlyList<MapItem> ordered)
    {
        DateTime? oldest = null, newest = null;
        foreach (var item in ordered)
        {
            if (item.State != MapState.Waiting)
                continue;
            oldest ??= item.Date;
            newest = item.Date;
        }
        return oldest is { } o && newest is { } n ? (o, n) : null;
    }
}
