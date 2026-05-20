using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class TitleNormalizerTests
{
    [Theory]
    [InlineData("Hello World", "hello world")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("Song (feat. Artist)", "song")]
    [InlineData("Song (Remix)", "song")]
    [InlineData("Song [Official Video]", "song")]
    [InlineData("Song ft. Someone", "song")]
    [InlineData("Hello, World!", "hello world")]
    [InlineData("  Extra   Spaces  ", "extra spaces")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Song (feat. X) [Remix]", "song")]
    public void NormalizeForSearch_MatchesLegacyBehavior(string? input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.NormalizeForSearch(input));
    }

    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("Mötley Crüe", "motley crue")]
    [InlineData("Sigur Rós", "sigur ros")]
    public void NormalizeForSearch_FoldsDiacritics(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.NormalizeForSearch(input));
    }

    [Fact]
    public void NormalizeForSearch_FoldsCyrillicLookalikes()
    {
        // "KoЯn" stylization → "korn"
        Assert.Equal("korn", TitleNormalizer.NormalizeForSearch("KoЯn"));
    }

    [Fact]
    public void FoldDiacritics_LeavesPlainAsciiUnchanged()
    {
        Assert.Equal("Plain Text 123", TitleNormalizer.FoldDiacritics("Plain Text 123"));
    }

    [Fact]
    public void FoldDiacritics_AccentEqualsUnaccented()
    {
        Assert.Equal(
            TitleNormalizer.FoldDiacritics("cafe"),
            TitleNormalizer.FoldDiacritics("café"));
    }
}
