using MusicHoarder.Api.Metadata;
using Xunit;

namespace MusicHoarder.Api.Tests.Metadata;

public class ReleaseDateParserTests
{
    [Theory]
    [InlineData("2019", 2019)]          // bare year (Spotify/Deezer album release_date)
    [InlineData("2019-03", 2019)]       // year-month
    [InlineData("2019-03-15", 2019)]    // full ISO date
    [InlineData("2001-03-12", 2001)]    // MusicBrainz-style date
    [InlineData("1971-11-08", 1971)]
    public void ParseYear_extracts_leading_year_from_release_date(string releaseDate, int expected)
    {
        Assert.Equal(expected, ReleaseDateParser.ParseYear(releaseDate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseYear_returns_null_for_missing_input(string? releaseDate)
    {
        Assert.Null(ReleaseDateParser.ParseYear(releaseDate));
    }

    [Theory]
    [InlineData("99")]          // too short to be a year
    [InlineData("abcd")]        // non-numeric leading token
    [InlineData("not-a-date")]
    public void ParseYear_returns_null_for_unparseable_input(string releaseDate)
    {
        Assert.Null(ReleaseDateParser.ParseYear(releaseDate));
    }

    [Theory]
    [InlineData("0050-01-01")]  // implausibly small — must not surface "50"
    [InlineData("0999-01-01")]
    [InlineData("3000-01-01")]  // upper bound is exclusive
    [InlineData("9999")]        // sentinel-style value some catalogs emit
    public void ParseYear_rejects_years_outside_the_plausible_range(string releaseDate)
    {
        Assert.Null(ReleaseDateParser.ParseYear(releaseDate));
    }

    [Theory]
    [InlineData("1001", 1001)]  // just inside the lower bound
    [InlineData("2999", 2999)]  // just inside the upper bound
    public void ParseYear_accepts_the_plausible_range_boundaries(string releaseDate, int expected)
    {
        Assert.Equal(expected, ReleaseDateParser.ParseYear(releaseDate));
    }
}
