using MusicHoarder.Api.Artwork;

namespace MusicHoarder.Api.Tests.Artwork;

public class ArtworkMatchVerifierTests
{
    [Theory]
    // Exact and near-exact identities.
    [InlineData("Daft Punk", "Discovery", "Daft Punk", "Discovery", true)]
    [InlineData("Beyoncé", "Lemonade", "Beyonce", "LEMONADE", true)]
    // Edition qualifiers extend the title at a word boundary.
    [InlineData("Daft Punk", "Discovery", "Daft Punk", "Discovery Deluxe Edition", true)]
    [InlineData("Daft Punk", "Discovery Deluxe Edition", "Daft Punk", "Discovery", true)]
    [InlineData("Daft Punk", "Discovery", "Daft Punk", "Discoveryland", false)]
    // The classic failure mode: a fuzzy hit on a compilation must be rejected.
    [InlineData("Daft Punk", "Discovery", "Various Artists", "Best 50 Dance Hits", false)]
    [InlineData("Daft Punk", "Discovery", "Various Artists", "Discovery", false)]
    // Credit joiners differ per catalog; token-subset bridges them.
    [InlineData("Metro Boomin & Future", "We Still Don't Trust You", "Metro Boomin, Future", "We Still Don't Trust You", true)]
    [InlineData("Tyler, The Creator", "Igor", "Tyler The Creator", "IGOR", true)]
    // A different artist with the same album title is rejected.
    [InlineData("Daft Punk", "Discovery", "Mr Oizo", "Discovery", false)]
    public void IsAlbumMatch(string queryArtist, string queryAlbum, string candArtist, string candAlbum, bool expected)
        => Assert.Equal(expected, ArtworkMatchVerifier.IsAlbumMatch(queryArtist, queryAlbum, candArtist, candAlbum));

    [Fact]
    public void WithoutQueryArtistOnlyExactTitleMatches()
    {
        Assert.True(ArtworkMatchVerifier.IsAlbumMatch(null, "Discovery", "Daft Punk", "Discovery"));
        Assert.False(ArtworkMatchVerifier.IsAlbumMatch(null, "Discovery", "Daft Punk", "Discovery Deluxe Edition"));
    }

    [Theory]
    [InlineData("Daft Punk", "Daft Punk", true)]
    [InlineData("Beyoncé", "Beyonce", true)]
    [InlineData("KoЯn", "Korn", true)]
    // Portraits need strict equality — "Drake" must not claim "Drake Bell"'s portrait.
    [InlineData("Drake", "Drake Bell", false)]
    [InlineData("Drake", "", false)]
    [InlineData("", "Drake", false)]
    public void IsArtistMatch(string query, string candidate, bool expected)
        => Assert.Equal(expected, ArtworkMatchVerifier.IsArtistMatch(query, candidate));
}
