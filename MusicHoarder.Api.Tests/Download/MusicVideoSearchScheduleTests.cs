using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

public class MusicVideoSearchScheduleTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(MusicVideoSearchOutcome.Clip, true, 0, 30)]
    [InlineData(MusicVideoSearchOutcome.Clip, false, 0, 180)]
    [InlineData(MusicVideoSearchOutcome.NoClip, true, 0, 7)]
    [InlineData(MusicVideoSearchOutcome.NoClip, false, 0, 120)]
    // Empty: one day (a recent release) or a week, doubling per consecutive empty search, capped.
    [InlineData(MusicVideoSearchOutcome.Empty, true, 1, 1)]
    [InlineData(MusicVideoSearchOutcome.Empty, true, 2, 2)]
    [InlineData(MusicVideoSearchOutcome.Empty, true, 3, 4)]
    [InlineData(MusicVideoSearchOutcome.Empty, true, 4, 8)]
    [InlineData(MusicVideoSearchOutcome.Empty, true, 5, 14)]
    [InlineData(MusicVideoSearchOutcome.Empty, true, 50, 14)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, 1, 7)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, 2, 14)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, 3, 28)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, 4, 56)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, 5, 112)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, 6, 180)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, 1000, 180)]
    // A count of zero or less is read as the first empty search.
    [InlineData(MusicVideoSearchOutcome.Empty, true, 0, 1)]
    [InlineData(MusicVideoSearchOutcome.Empty, false, -3, 7)]
    public void The_next_search_waits_as_long_as_the_outcome_and_release_age_say(
        MusicVideoSearchOutcome outcome, bool recent, int emptySearches, int expectedDays)
    {
        Assert.Equal(
            Now.AddDays(expectedDays),
            MusicVideoSearchSchedule.NextSearchAt(outcome, recent, emptySearches, Now));
    }

    [Theory]
    // The date's own precision decides: a bare year or month counts as its last day.
    [InlineData("2026", true)]
    [InlineData("2026-11", true)]
    [InlineData("2026-11-20", true)]
    [InlineData("2026-12-31", true)] // not out yet
    [InlineData("2026-08-28", true)] // just inside 90 days of 2026-11-25
    [InlineData("2026-08-26", false)]
    [InlineData("2026-08", true)] // its last day, 2026-08-31, is inside the window
    [InlineData("2026-07", false)]
    [InlineData("2025", false)] // Dec 2025 is older than 90 days in November 2026
    [InlineData("1999-03-14", false)]
    [InlineData("2026-11-20T00:00:00Z", true)]
    [InlineData(" 2026-11-20", true)]
    public void A_release_date_is_recent_within_ninety_days(string releaseDate, bool recent)
    {
        // Added long ago, so only the release date can make it recent.
        Assert.Equal(recent, MusicVideoSearchSchedule.IsRecentRelease(releaseDate, year: null, Now.AddYears(-3), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("26-11-2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-00")]
    [InlineData("2026-02-30")]
    [InlineData("0000")]
    public void Without_a_usable_release_date_the_day_it_was_added_decides(string? releaseDate)
    {
        Assert.True(MusicVideoSearchSchedule.IsRecentRelease(releaseDate, year: null, Now.AddDays(-89), Now));
        Assert.False(MusicVideoSearchSchedule.IsRecentRelease(releaseDate, year: null, Now.AddDays(-91), Now));
        Assert.False(MusicVideoSearchSchedule.IsRecentRelease(releaseDate, year: null, null, Now));
    }

    [Fact]
    public void A_usable_release_date_wins_over_the_day_it_was_added()
    {
        // Added yesterday, but released in 2015: a catalogue song, however fresh in the library.
        Assert.False(MusicVideoSearchSchedule.IsRecentRelease("2015-06-01", year: 2026, Now.AddDays(-1), Now));
    }

    [Theory]
    [InlineData(2026, true)]
    [InlineData(2025, false)]
    [InlineData(2015, false)]
    public void Without_a_release_date_the_year_decides_before_the_day_it_was_added(int year, bool recent)
    {
        Assert.Equal(recent, MusicVideoSearchSchedule.IsRecentRelease(null, year, Now.AddDays(-1), Now));
    }
}
