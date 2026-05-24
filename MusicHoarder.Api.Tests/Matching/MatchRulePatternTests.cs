using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class MatchRulePatternTests
{
    [Fact]
    public void Matches_ArtistTitleChannel_ExtractsFields()
    {
        var extraction = MatchRulePattern.Match("{artist} | {title} | 101Barz", "Yung Nnelg | Wintersessie 2020 | 101Barz");

        Assert.NotNull(extraction);
        Assert.Equal("Yung Nnelg", extraction!.Artist);
        Assert.Equal("Wintersessie 2020", extraction.Title);
        Assert.Null(extraction.Album);
        Assert.Null(extraction.AlbumArtist);
    }

    [Fact]
    public void Matches_CapturesAlbumPlaceholder()
    {
        var extraction = MatchRulePattern.Match("{artist} - {title} [{album}]", "Drake - Energy [If You're Reading This]");

        Assert.NotNull(extraction);
        Assert.Equal("Drake", extraction!.Artist);
        Assert.Equal("Energy", extraction.Title);
        Assert.Equal("If You're Reading This", extraction.Album);
    }

    [Fact]
    public void Matches_IsWhitespaceTolerant_AndCaseInsensitiveOnLiterals()
    {
        // Extra spacing in the input + lowercase channel literal still match.
        var extraction = MatchRulePattern.Match("{artist} | {title} | 101Barz", "Yung Nnelg  |  Wintersessie 2020  |  101barz");

        Assert.NotNull(extraction);
        Assert.Equal("Yung Nnelg", extraction!.Artist);
        Assert.Equal("Wintersessie 2020", extraction.Title);
    }

    [Fact]
    public void NonMatchingSample_ReturnsNull()
    {
        Assert.Null(MatchRulePattern.Match("{artist} | {title} | 101Barz", "Some random song title"));
    }

    [Theory]
    [InlineData("{artist} | {title} | 101Barz")]
    [InlineData("{title}")]
    public void TryCompile_ValidTemplates_Succeed(string template)
    {
        Assert.True(MatchRulePattern.TryCompile(template, out var compiled, out var error));
        Assert.NotNull(compiled);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("just literal text", "placeholder")]
    [InlineData("{artist} | {title", "Unclosed")]
    [InlineData("{artist} | {artist}", "more than once")]
    [InlineData("{channel} - {title}", "Unknown placeholder")]
    public void TryCompile_InvalidTemplates_FailWithError(string template, string expectedFragment)
    {
        Assert.False(MatchRulePattern.TryCompile(template, out var compiled, out var error));
        Assert.Null(compiled);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidTemplate_MatchReturnsNull()
    {
        Assert.Null(MatchRulePattern.Match("no placeholders here", "anything"));
    }
}
