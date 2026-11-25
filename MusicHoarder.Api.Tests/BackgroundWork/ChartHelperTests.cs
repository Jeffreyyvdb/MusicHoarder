using MusicHoarder.Api.BackgroundWork.Charts;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class ChartHelperTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Daily_buckets_are_contiguous_utc_days_ending_today_with_zeros_filled()
    {
        var points = TimeBuckets.Fill(
            Now, hourly: false, count: 5, seriesCount: 1,
            [(Now.AddDays(-4).AddHours(-3), 0, 10), (Now.AddHours(-2), 0, 4), (Now.AddHours(-1), 0, 1)]);

        Assert.Equal(5, points.Count);
        Assert.Equal(new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), points[0].StartUtc);
        Assert.Equal(new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc), points[^1].StartUtc);
        Assert.Equal([10.0, 0, 0, 0, 5], points.Select(p => p.Values[0]));
    }

    [Fact]
    public void Forward_buckets_start_today_put_overdue_work_on_today_and_drop_what_is_past_the_last_day()
    {
        var points = TimeBuckets.FillForward(
            Now, count: 4, seriesCount: 2,
            [
                (Now.AddDays(-9), 0, 1),
                (Now.AddHours(1), 0, 2),
                (Now.AddDays(2).AddHours(-5), 1, 3),
                (Now.AddDays(3).AddHours(9), 1, 4),
                (Now.AddDays(4), 0, 99),
                (Now.AddDays(1), 5, 99),
            ]);

        Assert.Equal(new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc), points[0].StartUtc);
        Assert.Equal(new DateTime(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc), points[^1].StartUtc);
        Assert.Equal([3.0, 0], points[0].Values);
        Assert.Equal([0.0, 3], points[2].Values);
        Assert.Equal([0.0, 4], points[3].Values);
    }

    [Theory]
    [InlineData(0, "1 KB")]
    [InlineData(900 * 1024, "900 KB")]
    [InlineData(143L * 1024 * 1024, "143 MB")]
    [InlineData(2.5 * 1024 * 1024 * 1024, "2.5 GB")]
    [InlineData(20L * 1024 * 1024 * 1024, "20 GB")]
    [InlineData(3.25 * 1024 * 1024 * 1024 * 1024, "3.3 TB")]
    public void Bytes_read_in_the_binary_units_the_app_uses(double bytes, string expected)
        => Assert.Equal(expected, MusicHoarder.Api.BackgroundWork.BackgroundText.Bytes(bytes));

    [Fact]
    public void A_sample_older_than_the_first_bucket_is_dropped_not_folded_into_it()
    {
        var points = TimeBuckets.Fill(Now, hourly: false, count: 3, seriesCount: 1, [(Now.AddDays(-9), 0, 99)]);

        Assert.All(points, p => Assert.Equal(0, p.Values[0]));
    }

    [Fact]
    public void Hourly_buckets_split_series_and_start_on_the_hour()
    {
        var points = TimeBuckets.Fill(
            Now, hourly: true, count: 3, seriesCount: 2,
            [(new DateTime(2026, 11, 25, 12, 59, 0, DateTimeKind.Utc), 0, 30),
             (new DateTime(2026, 11, 25, 13, 0, 0, DateTimeKind.Utc), 1, 7),
             (new DateTime(2026, 11, 25, 14, 5, 0, DateTimeKind.Utc), 1, 2),
             (new DateTime(2026, 11, 25, 14, 6, 0, DateTimeKind.Utc), 1, 3)]);

        Assert.Equal(new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc), points[0].StartUtc);
        Assert.Equal([30.0, 0], points[0].Values);
        Assert.Equal([0.0, 7], points[1].Values);
        Assert.Equal([0.0, 5], points[2].Values);
    }

    [Fact]
    public void An_unknown_series_index_is_ignored()
    {
        var points = TimeBuckets.Fill(Now, hourly: true, count: 1, seriesCount: 1, [(Now, 3, 1), (Now, -1, 1)]);

        Assert.Equal([0.0], points[0].Values);
    }

    [Fact]
    public void Daily_counts_give_one_value_per_day_and_the_trend_length()
    {
        var counts = TimeBuckets.DailyCounts(Now, TimeBuckets.TrendDays, [Now, Now.AddMinutes(-5), Now.AddDays(-1)]);

        Assert.Equal(14, counts.Count);
        Assert.Equal(2, counts[^1]);
        Assert.Equal(1, counts[^2]);
    }

    [Fact]
    public void Eta_is_the_backlog_over_the_rate_and_null_without_either()
    {
        Assert.Equal(Now.AddDays(10), EtaEstimator.Eta(1000, 100, Now));
        Assert.Null(EtaEstimator.Eta(1000, 0, Now));
        Assert.Null(EtaEstimator.Eta(0, 100, Now));
    }

    [Fact]
    public void Eta_beyond_ten_years_is_no_estimate()
    {
        Assert.Null(EtaEstimator.Eta(1_000_000, 1, Now));
    }

    [Fact]
    public void Rate_is_spread_over_the_days_the_evidence_covers_when_it_is_younger_than_the_window()
    {
        // 700 done, but the record only reaches back 2 days: 350 a day, not 100.
        Assert.Equal(350, EtaEstimator.RatePerDay(700, Now.AddDays(-2), Now, 7));
        // Evidence older than the window: the window.
        Assert.Equal(100, EtaEstimator.RatePerDay(700, Now.AddDays(-30), Now, 7));
        // A record of a few minutes is not extrapolated to a per-minute rate.
        Assert.Equal(700, EtaEstimator.RatePerDay(700, Now.AddMinutes(-5), Now, 7));
        Assert.Equal(0, EtaEstimator.RatePerDay(0, Now.AddDays(-2), Now, 7));
    }

    private static List<MapItem> Items(int n, Func<int, MapState> state)
        => [.. Enumerable.Range(0, n).Select(i => new MapItem(Now.AddDays(i - n), state(i)))];

    [Fact]
    public void Map_bins_hold_equal_song_counts_and_never_more_than_48()
    {
        var bins = LibraryMapBinner.Bin(Items(1000, _ => MapState.Done));

        Assert.Equal(48, bins.Count);
        var sizes = bins.Select(b => b.Done).ToList();
        Assert.Equal(1000, sizes.Sum());
        Assert.InRange(sizes.Max() - sizes.Min(), 0, 1);
    }

    [Fact]
    public void A_small_library_gets_a_bin_per_song_and_counts_each_state()
    {
        var bins = LibraryMapBinner.Bin(Items(3, i => (MapState)i));

        Assert.Equal(3, bins.Count);
        Assert.Equal((1, 0, 0), (bins[0].Done, bins[0].Waiting, bins[0].Excluded));
        Assert.Equal((0, 1, 0), (bins[1].Done, bins[1].Waiting, bins[1].Excluded));
        Assert.Equal((0, 0, 1), (bins[2].Done, bins[2].Waiting, bins[2].Excluded));
    }

    [Fact]
    public void Bins_run_oldest_first_and_span_their_songs_dates()
    {
        var items = Items(96, _ => MapState.Waiting);

        var bins = LibraryMapBinner.Bin(items);

        Assert.Equal(items[0].Date, bins[0].FromUtc);
        Assert.Equal(items[1].Date, bins[0].ToUtc);
        Assert.Equal(items[^1].Date, bins[^1].ToUtc);
        Assert.True(bins.Zip(bins.Skip(1)).All(p => p.First.ToUtc <= p.Second.FromUtc));
    }

    [Fact]
    public void An_empty_library_has_no_bins_and_no_frontier()
    {
        Assert.Empty(LibraryMapBinner.Bin([]));
        Assert.Null(LibraryMapBinner.WaitingFrontier([]));
    }

    [Fact]
    public void Frontier_is_where_the_first_and_last_waiting_songs_sit()
    {
        // Two ends have been worked: done, done, waiting x4, done, done.
        var items = Items(8, i => i is >= 2 and <= 5 ? MapState.Waiting : MapState.Done);

        var frontier = LibraryMapBinner.WaitingFrontier(items);

        Assert.Equal(items[2].Date, frontier!.Value.Oldest);
        Assert.Equal(items[5].Date, frontier.Value.Newest);
    }

    [Fact]
    public void No_waiting_songs_means_no_frontier()
    {
        Assert.Null(LibraryMapBinner.WaitingFrontier(Items(5, _ => MapState.Done)));
    }
}
