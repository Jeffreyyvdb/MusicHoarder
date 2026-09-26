using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class CanonicalAlbumMatchTests
{
    private const double Threshold = 85;

    [Fact]
    public void ADifferentAlbumTheSearchFound_IsNotTheAlbum()
    {
        // The prod chain: an unreleased album no catalog carries, answered with whatever came up.
        Assert.False(IsSameAlbum("CHIRAQ", "Kanye West", ["Awesome"],
            "Pride 'n' Joy (feat. Kanye West, Miguel) - Single", "Fat Joe", ["Pride 'n' Joy"]));
        Assert.False(IsSameAlbum("CHIRAQ", "Rigo Dominguez Y Su Grupo Audaz", ["Awesome"],
            "20 Éxitos Bailables", "Rigo Dominguez y Su Grupo Audaz", ["Macumba", "La Morena"]));
    }

    [Fact]
    public void AnEditionOfTheAlbumBySameArtist_IsTheAlbum()
    {
        Assert.True(IsSameAlbum("Graduation", "Kanye West", [],
            "Graduation (Alternative Business Partners)", "Kanye West", []));
        Assert.True(IsSameAlbum("The Miseducation of Lauryn Hill", "Lauryn Hill", [],
            "The Miseducation of Lauryn Hill", "Ms. Lauryn Hill", []));
    }

    [Fact]
    public void SameTitleByAnotherArtist_IsNotTheAlbum_UnlessItHoldsOneOfTheOwnersTracks()
    {
        // Kanye's leaked "So Help Me God" is not 2 Chainz's record of that name...
        Assert.False(IsSameAlbum("So Help Me God", "Kanye West", ["Only One", "Southside Serenade"],
            "So Help Me God!", "2 Chainz", ["Lambo Wrist", "Grey Area"]));
        // ...but a featured artist's song on someone else's album is on that album.
        Assert.True(IsSameAlbum("Relapse: Refill", "Drake", ["Forever"],
            "Relapse: Refill", "Eminem", ["Forever", "Crack a Bottle"]));
    }

    [Fact]
    public void ATitleTrack_DoesNotCountAsASharedTrack()
    {
        // Juice WRLD's "What Is Love?" and Jaymes Young's single of that name share only the title.
        Assert.False(IsSameAlbum("What Is Love?", "Juice WRLD", ["What Is Love?"],
            "What Is Love - Single", "Jaymes Young", ["What Is Love", "What Is Love (Acoustic)"]));
    }

    [Fact]
    public void AShortNameInsideALongOne_IsNotTheSameName()
    {
        // Partial fuzzy matching read both of these as 90: "Die Man" in "Die Manen", "Ka" in "Kaiser".
        Assert.False(CanonicalAlbumMatch.TitleMatches(
            "Die Man", "Rückwärtswalzer oder Die Manen der Familie Prischinger (Ungekürzte Lesung)", Threshold));
        Assert.False(CanonicalAlbumMatch.SameArtist("KA", "Vea Kaiser", Threshold));
        Assert.False(CanonicalAlbumMatch.TitleMatches("Yeezus Tour", "ye", Threshold));
    }

    [Theory]
    [InlineData("Kanye West", "Kanye West & Dr. Dre")]
    [InlineData("Domo Genesis", "Domo Genesis, The Alchemist")]
    [InlineData("Future & Metro Boomin", "Future, Metro Boomin")]
    [InlineData("Various Artists", "Verschiedene Interpreten")]
    public void SpellingsAndCollaborations_AreTheSameArtist(string artist, string candidate) =>
        Assert.True(CanonicalAlbumMatch.SameArtist(artist, candidate, Threshold));

    [Fact]
    public void AMissingTitleOrArtist_IsNoEvidenceAgainst()
    {
        Assert.True(CanonicalAlbumMatch.TitleMatches("Discovery", null, Threshold));
        Assert.True(CanonicalAlbumMatch.SameArtist(null, "Daft Punk", Threshold));
    }

    private static bool IsSameAlbum(
        string album, string artist, string?[] owned, string candidateTitle, string candidateArtist, string?[] tracks) =>
        CanonicalAlbumMatch.IsSameAlbum(album, artist, owned, candidateTitle, candidateArtist, tracks, Threshold, Threshold);
}
