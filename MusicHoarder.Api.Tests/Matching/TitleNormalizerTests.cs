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

    [Theory]
    [InlineData("JAY-Z", "jayz")]
    [InlineData("Jaÿ-z", "jayz")]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("  OutKast  ", "outkast")]
    [InlineData("Nas (featuring AZ)", "nas featuring az")]
    [InlineData("Nas feat. AZ", "nas feat az")]
    [InlineData("Nas [ft AZ]", "nas ft az")]
    [InlineData("2Pac + Outlawz", "2pac outlawz")]
    [InlineData(null, "")]
    public void NormalizeArtistKey_FoldsSpellingButKeepsExtraArtists(string? input, string expected)
    {
        // Unlike the search form, a featuring clause or bracketed text is part of the key: it names
        // more artists, so "Nas (featuring AZ)" must never key as plain "nas".
        Assert.Equal(expected, TitleNormalizer.NormalizeArtistKey(input));
    }
}
