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
    [InlineData("Stronger (Live Version)", "Stronger")]
    [InlineData("Stronger (Remix Version)", "Stronger")]
    [InlineData("Stronger (feat. Daft Punk) (Live)", "Stronger")]
    [InlineData("Stronger Remix", "Stronger")]
    [InlineData("Harder", "Stronger")]
    public void A_different_recording_does_not_match(string catalog, string song)
    {
        Assert.NotEqual(ReferenceTitle.Normalize(song), ReferenceTitle.Normalize(catalog));
    }

    [Fact]
    public void A_title_in_another_script_matches_only_itself()
    {
        Assert.Equal("夜に駆ける", ReferenceTitle.Normalize("夜に駆ける (feat. ikura)"));
        Assert.NotEqual(ReferenceTitle.Normalize("夜に駆ける"), ReferenceTitle.Normalize("群青"));
        Assert.Equal("", ReferenceTitle.Normalize("!!!"));
    }

    [Theory]
    [InlineData("Denzel Curry, TiaCorine, A$AP Ferg", "Denzel Curry, TiaCorine & A$AP Ferg", true)]
    [InlineData("Kid Cudi, Kanye West, Common", "Kid Cudi", true)]
    [InlineData("Tyler, The Creator", "Tyler, The Creator", true)]
    [InlineData("Beyoncé", "Beyonce", true)]
    [InlineData("Kanye West", "Kanye West Tribute Band", true)]
    [InlineData("Kanye West", "The Weeknd", false)]
    [InlineData("Drake", "Drakeo the Ruler", false)]
    [InlineData("Drake", null, false)]
    public void A_catalog_credit_must_name_the_songs_lead_artist(string song, string? catalog, bool same)
    {
        Assert.Equal(same, ReferenceTitle.SameArtist(song, catalog));
    }

    [Fact]
    public void The_search_uses_the_lead_artist_and_the_title_without_brackets()
    {
        Assert.Equal("Denzel Curry", ReferenceTitle.LeadArtist("Denzel Curry, TiaCorine"));
        Assert.Equal("HOT ONE ", ReferenceTitle.WithoutBrackets("HOT ONE (with TiaCorine)"));
    }
}
