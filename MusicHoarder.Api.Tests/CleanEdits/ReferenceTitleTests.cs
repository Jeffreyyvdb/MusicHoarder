using MusicHoarder.Api.CleanEdits;

namespace MusicHoarder.Api.Tests.CleanEdits;

public class ReferenceTitleTests
{
    [Theory]
    [InlineData("HOT ONE (with TiaCorine & A$AP Ferg)", "Hot One")]
    [InlineData("Stronger", "stronger")]
    [InlineData("Make Her Say (feat. Common & Kanye West)", "Make Her Say")]
    [InlineData("Créme Brûlée [Explicit Version]", "Creme Brulee")]
    [InlineData("Wandered To LA (with Justin Bieber)", "WANDERED TO LA")]
    public void A_catalog_title_matches_the_song_despite_credits_and_edition_labels(string catalog, string song)
    {
        Assert.Equal(ReferenceTitle.Normalize(song), ReferenceTitle.Normalize(catalog));
    }

    [Theory]
    [InlineData("Stronger (Live)", "Stronger")]
    [InlineData("Stronger Remix", "Stronger")]
    [InlineData("Harder", "Stronger")]
    public void A_different_recording_does_not_match(string catalog, string song)
    {
        Assert.NotEqual(ReferenceTitle.Normalize(song), ReferenceTitle.Normalize(catalog));
    }

    [Fact]
    public void The_search_uses_the_lead_artist_and_the_title_without_brackets()
    {
        Assert.Equal("Denzel Curry", ReferenceTitle.LeadArtist("Denzel Curry, TiaCorine"));
        Assert.Equal("HOT ONE ", ReferenceTitle.WithoutBrackets("HOT ONE (with TiaCorine)"));
    }
}
