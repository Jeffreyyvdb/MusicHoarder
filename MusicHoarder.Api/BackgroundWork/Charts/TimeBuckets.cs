namespace MusicHoarder.Api.BackgroundWork.Charts;

public static class TimeBuckets
{
    public const int TrendDays = 14;

    public static IReadOnlyList<DateTime> Starts(DateTime nowUtc, bool hourly, int count)
    {
        var current = hourly ? FloorToHour(nowUtc) : DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);
        var step = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        return [.. Enumerable.Range(0, count).Select(i => current - step * (count - 1 - i))];
    }

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

    public static IReadOnlyList<SeriesPointDto> FillForward(
        DateTime nowUtc, int count, int seriesCount, IEnumerable<(DateTime At, int Series, double Value)> samples)
    {
        var first = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);
        var values = Enumerable.Range(0, count).Select(_ => new double[seriesCount]).ToArray();

        foreach (var (at, series, value) in samples)
        {
            if (series < 0 || series >= seriesCount)
                continue;
            var index = at <= first ? 0 : (int)((at - first).Ticks / TimeSpan.TicksPerDay);
            if (index < count)
                values[index][series] += value;
        }

        return [.. values.Select((v, i) => new SeriesPointDto(first.AddDays(i), v))];
    }

    public static IReadOnlyList<double> DailyTotals(DateTime nowUtc, int days, IEnumerable<(DateTime At, double Value)> samples)
        => [.. Fill(nowUtc, hourly: false, days, 1, samples.Select(s => (s.At, 0, s.Value))).Select(p => p.Values[0])];

    public static IReadOnlyList<double> DailyCounts(DateTime nowUtc, int days, IEnumerable<DateTime> times)
        => DailyTotals(nowUtc, days, times.Select(t => (t, 1.0)));

    private static DateTime FloorToHour(DateTime t)
        => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
}
