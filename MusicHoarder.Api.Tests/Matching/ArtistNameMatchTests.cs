using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class ArtistNameMatchTests
{
    private const double Threshold = 85;

    [Theory]
    [InlineData("Beyonce", "Beyoncé")]
    [InlineData("AC/DC", "ACDC")]
    [InlineData("¥$", "¥$")]
    public void SpellingsOfOneName_AreTheSame(string name, string other)
    {
        Assert.True(ArtistNameMatch.SameSpelling(name, other, Threshold));
    }

    [Theory]
    [InlineData("P!nk", "!")]
    [InlineData("Ke$ha", "$")]
    [InlineData("!!!", "!!")]
    [InlineData("!!!!!!!", "!!!!!!")]
    [InlineData("Radiohead", "(Radiohead)")]
    [InlineData("Joey", "Joey Bada$$")]
    [InlineData("Artist", null)]
    public void DifferentNames_AreNotTheSame(string name, string? other)
    {
        Assert.False(ArtistNameMatch.SameSpelling(name, other, Threshold));
    }
}
