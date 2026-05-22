using MusicHoarder.Api.Enrichment.Providers;

namespace MusicHoarder.Api.Tests.Enrichment;

public class JuiceWrldTrackerServiceTests
{
    [Theory]
    [InlineData("3:42", 222)]
    [InlineData("0:30", 30)]
    [InlineData("1:02:03", 3723)]
    public void ParseLength_ValidDurations(string input, double expected)
        => Assert.Equal(expected, JuiceWrldTrackerService.ParseLength(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("3")]
    [InlineData("3:")]
    [InlineData("1:2:3:4")]
    public void ParseLength_InvalidDurations_ReturnNull(string? input)
        => Assert.Null(JuiceWrldTrackerService.ParseLength(input));

    [Theory]
    [InlineData("Recorded\r\nJanuary 19, 2016.", 2016)]
    [InlineData("First Previewed\r\nDecember 2017", 2017)]
    [InlineData("released 1999", 1999)]
    public void ParseYear_ExtractsFourDigitYear(string input, int expected)
        => Assert.Equal(expected, JuiceWrldTrackerService.ParseYear(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no year here")]
    [InlineData("track 42")]
    public void ParseYear_NoYear_ReturnsNull(string? input)
        => Assert.Null(JuiceWrldTrackerService.ParseYear(input));
}
