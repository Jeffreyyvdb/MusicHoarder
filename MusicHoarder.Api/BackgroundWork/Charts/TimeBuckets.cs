namespace MusicHoarder.Api.BackgroundWork.Charts;

/// <summary>
/// Fixed UTC buckets for the series panels: contiguous, oldest first, ending with the bucket that holds
/// "now", with every empty bucket present as zeros so a quiet hour is a gap in the data, not in the axis.
/// </summary>
public static class TimeBuckets
{
    public const int TrendDays = 14;

    public static IReadOnlyList<DateTime> Starts(DateTime nowUtc, bool hourly, int count)
    {
        var current = hourly ? FloorToHour(nowUtc) : DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);
        var step = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        return [.. Enumerable.Range(0, count).Select(i => current - step * (count - 1 - i))];
    }

    /// <summary>One point per bucket; a sample outside every bucket is dropped, and so is one for an unknown series.</summary>
    public static IReadOnlyList<SeriesPointDto> Fill(
        DateTime nowUtc, bool hourly, int count, int seriesCount, IEnumerable<(DateTime At, int Series, double Value)> samples)
    {
        var starts = Starts(nowUtc, hourly, count);
        var values = starts.Select(_ => new double[seriesCount]).ToArray();
        var first = starts[0];
        var step = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);

        foreach (var (at, series, value) in samples)
        {
            if (at < first || series < 0 || series >= seriesCount)
                continue;
            var index = (int)((at - first).Ticks / step.Ticks);
            if (index < starts.Count)
                values[index][series] += value;
        }

        return [.. starts.Select((start, i) => new SeriesPointDto(start, values[i]))];
    }

    /// <summary>Totals per UTC day for the last <paramref name="days"/> days, oldest first.</summary>
    public static IReadOnlyList<double> DailyTotals(DateTime nowUtc, int days, IEnumerable<(DateTime At, double Value)> samples)
        => [.. Fill(nowUtc, hourly: false, days, 1, samples.Select(s => (s.At, 0, s.Value))).Select(p => p.Values[0])];

    public static IReadOnlyList<double> DailyCounts(DateTime nowUtc, int days, IEnumerable<DateTime> times)
        => DailyTotals(nowUtc, days, times.Select(t => (t, 1.0)));

    private static DateTime FloorToHour(DateTime t)
        => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
}
