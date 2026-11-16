using MusicHoarder.Api.Listening;

namespace MusicHoarder.Api.Tests.Listening;

public class ListeningStatsCalculatorTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static ListeningEventRow Play(
        int songId, DateTime at, string? artist = "Artist", string? album = "Album",
        string? albumArtist = null, string? genre = null, int? seconds = 180, string? title = null) =>
        new(songId, at, title ?? $"Song {songId}", artist, albumArtist, album, genre, seconds);

    private static ListeningStats Compute(
        IReadOnlyList<ListeningEventRow> events, TimeSpan? offset = null,
        ListeningRange range = ListeningRange.FourWeeks) =>
        ListeningStatsCalculator.Compute(
            events, events, range, Now, offset ?? TimeSpan.Zero, events.Count == 0 ? null : events.Min(e => e.PlayedAtUtc));

    [Fact]
    public void Empty_history_gives_zeroes_and_no_facts()
    {
        var stats = Compute([]);

        Assert.Equal(0, stats.Totals.Streams);
        Assert.Empty(stats.TopTracks);
        Assert.Empty(stats.Days);
        Assert.Equal(new int[24], stats.Clock);
        Assert.Null(stats.TrackingSinceUtc);
        Assert.Null(stats.Facts.LongestStreak);
        Assert.Null(stats.Facts.PeakHour);
        Assert.Null(stats.Facts.NightOwlShare);
        Assert.Equal(0, stats.Facts.CurrentStreakDays);
    }

    [Fact]
    public void Totals_count_streams_minutes_and_distinct_things()
    {
        var day = Now.AddDays(-1);
        var stats = Compute([
            Play(1, day, artist: "A feat. B", album: "X", seconds: 90),
            Play(1, day.AddHours(1), artist: "A feat. B", album: "X", seconds: 90),
            Play(2, day.AddHours(2), artist: "b", album: "Y", seconds: null),
            Play(3, day.AddHours(3), artist: "C", album: null, seconds: 120),
        ]);

        Assert.Equal(4, stats.Totals.Streams);
        Assert.Equal(5, stats.Totals.Minutes);
        Assert.Equal(3, stats.Totals.DistinctTracks);
        Assert.Equal(3, stats.Totals.DistinctArtists);
        Assert.Equal(2, stats.Totals.DistinctAlbums);
    }

    [Fact]
    public void Credits_count_for_each_artist_and_the_commonest_spelling_wins()
    {
        var day = Now.AddDays(-1);
        var stats = Compute([
            Play(1, day, artist: "Drake feat. Future"),
            Play(2, day.AddHours(1), artist: "Future"),
            Play(2, day.AddHours(2), artist: "FUTURE"),
        ]);

        var future = stats.TopArtists[0];
        Assert.Equal("Future", future.Name);
        Assert.Equal(3, future.Streams);
        Assert.Equal(2, future.SongId);
        Assert.Equal("Drake", stats.TopArtists[1].Name);
    }

    [Fact]
    public void Top_tracks_order_by_streams_then_minutes_then_recency()
    {
        var day = Now.AddDays(-2);
        var stats = Compute([
            Play(1, day, seconds: 60),
            Play(2, day.AddHours(1), seconds: 600),
            Play(3, day.AddHours(2), seconds: 600),
            Play(4, day.AddHours(3), seconds: 60),
            Play(4, day.AddHours(4), seconds: 60),
        ]);

        Assert.Equal([4, 3, 2, 1], stats.TopTracks.Select(t => t.SongId));
    }

    [Fact]
    public void Top_lists_are_capped()
    {
        var events = Enumerable.Range(1, 40)
            .Select(i => Play(i, Now.AddHours(-i), artist: $"Artist {i}", album: $"Album {i}", genre: $"G{i}"))
            .ToList();
        var stats = Compute(events);

        Assert.Equal(10, stats.TopTracks.Count);
        Assert.Equal(10, stats.TopArtists.Count);
        Assert.Equal(10, stats.TopAlbums.Count);
        Assert.Equal(12, stats.TopGenres.Count);
        Assert.Equal(40, stats.Recent.Count);
    }

    [Fact]
    public void Recent_is_newest_first_and_limited_to_fifty()
    {
        var events = Enumerable.Range(1, 70).Select(i => Play(i, Now.AddMinutes(-i))).ToList();
        var recent = Compute(events).Recent;

        Assert.Equal(50, recent.Count);
        Assert.Equal(1, recent[0].SongId);
    }

    [Fact]
    public void Genres_split_on_the_multi_value_delimiter()
    {
        var day = Now.AddDays(-1);
        var stats = Compute([
            Play(1, day, genre: "Hip Hop; Rap"),
            Play(2, day.AddHours(1), genre: "rap"),
        ]);

        Assert.Equal(("Rap", 2), (stats.TopGenres[0].Name, stats.TopGenres[0].Streams));
        Assert.Equal("Hip Hop", stats.TopGenres[1].Name);
    }

    [Fact]
    public void Albums_group_by_album_artist_and_ignore_case()
    {
        var day = Now.AddDays(-1);
        var stats = Compute([
            Play(1, day, artist: "A feat. B", albumArtist: "A", album: "Rec"),
            Play(2, day.AddHours(1), artist: "A", albumArtist: null, album: "rec"),
        ]);

        var album = Assert.Single(stats.TopAlbums);
        Assert.Equal(2, album.Streams);
        Assert.Equal("A", album.AlbumArtist);
    }

    [Fact]
    public void The_offset_moves_a_play_across_midnight_in_clock_weekday_and_day()
    {
        var late = new DateTime(2026, 11, 14, 23, 30, 0, DateTimeKind.Utc);

        var utc = Compute([Play(1, late)]);
        Assert.Equal(1, utc.Clock[23]);
        Assert.Equal("2026-11-14", Assert.Single(utc.Days).Date);
        Assert.Equal(1, utc.Weekdays[5]);

        var plusTwo = Compute([Play(1, late)], TimeSpan.FromHours(2));
        Assert.Equal(1, plusTwo.Clock[1]);
        Assert.Equal("2026-11-15", Assert.Single(plusTwo.Days).Date);
        Assert.Equal(1, plusTwo.Weekdays[6]);
        Assert.Equal(1.0, plusTwo.Facts.NightOwlShare);
        Assert.Equal(1, plusTwo.Facts.PeakHour);
    }

    [Fact]
    public void Streaks_count_consecutive_local_days()
    {
        var stats = Compute([
            Play(1, new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc)),
            Play(1, new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc)),
            Play(1, new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc)),
            Play(1, new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc)),
            Play(1, new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc)),
        ]);

        var longest = stats.Facts.LongestStreak!;
        Assert.Equal((3, "2026-11-01", "2026-11-03"), (longest.Days, longest.Start, longest.End));
        Assert.Equal(2, stats.Facts.CurrentStreakDays);
    }

    [Fact]
    public void A_streak_that_ended_before_yesterday_is_not_current()
    {
        var stats = Compute([Play(1, Now.AddDays(-3))]);
        Assert.Equal(0, stats.Facts.CurrentStreakDays);
    }

    [Fact]
    public void Most_repeated_needs_two_plays_of_one_song_on_one_day()
    {
        var day = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        var once = Compute([Play(1, day), Play(2, day.AddHours(1))]);
        Assert.Null(once.Facts.MostRepeated);

        var repeated = Compute([Play(1, day), Play(1, day.AddHours(1)), Play(1, day.AddHours(2)), Play(2, day)]);
        var fact = repeated.Facts.MostRepeated!;
        Assert.Equal((1, 3, "2026-11-10"), (fact.SongId, fact.Streams, fact.Date));
        Assert.Equal(new BusiestDay("2026-11-10", 4), repeated.Facts.BusiestDay);
    }

    [Fact]
    public void The_range_reports_its_start_and_lifetime_has_none()
    {
        Assert.Equal(Now.AddDays(-28), Compute([]).FromUtc);
        Assert.Null(Compute([], range: ListeningRange.Lifetime).FromUtc);
        Assert.Equal("1y", Compute([], range: ListeningRange.Year).Range);
    }
}
