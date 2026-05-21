using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class MetadataQualityHeuristicsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Track 03")]
    [InlineData("track03")]
    [InlineData("03")]
    [InlineData("Unknown Artist")]
    [InlineData("unknown")]
    [InlineData("Untitled")]
    [InlineData("Various Artists")]
    [InlineData("Visit www.example.com")]
    public void IsLowQuality_FlagsJunk(string? value)
    {
        Assert.True(MetadataQualityHeuristics.IsLowQuality(value));
    }

    [Theory]
    [InlineData("One More Time")]
    [InlineData("Beyoncé")]
    [InlineData("M83")]               // legit short/numeric-containing name, not a bare track number
    [InlineData("99 Luftballons")]
    public void IsLowQuality_KeepsRealValues(string value)
    {
        Assert.False(MetadataQualityHeuristics.IsLowQuality(value));
    }

    [Fact]
    public void IsLowQuality_FlagsValueEqualToFileName()
    {
        Assert.True(MetadataQualityHeuristics.IsLowQuality("01 - mystery", "01 - mystery.mp3"));
        Assert.True(MetadataQualityHeuristics.IsLowQuality("01 - mystery.mp3", "01 - mystery.mp3"));
    }
}
