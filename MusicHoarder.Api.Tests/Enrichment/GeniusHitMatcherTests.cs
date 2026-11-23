using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

public class GeniusHitMatcherTests
{
    private const double Threshold = 85;

    private static GeniusHit Hit(
        int id, string title, string artist, string state = "complete", bool instrumental = false) =>
        new(id, title, artist, state, instrumental);

    private static GeniusHit? Pick(string title, string artist, params GeniusHit[] hits) =>
        GeniusHitMatcher.Pick(hits, title, artist, Threshold, Threshold);

    [Fact]
    public void TranslationPage_IsSkippedForTheOriginal()
    {
        var picked = Pick("Tití Me Preguntó", "Bad Bunny",
            Hit(1, "Bad Bunny - Tití Me Preguntó (English Translation)", "Genius English Translations"),
            Hit(2, "Tití Me Preguntó", "Bad Bunny"));

        Assert.Equal(2, picked?.Id);
    }

    [Fact]
    public void RemixHit_NeverMatchesAPlainTitle()
    {
        var picked = Pick("Lucid Dreams", "Juice WRLD",
            Hit(1, "Lucid Dreams (Remix)", "Juice WRLD"),
            Hit(2, "Lucid Dreams", "Juice WRLD"));

        Assert.Equal(2, picked?.Id);
    }

    [Fact]
    public void PlainHit_NeverMatchesARemixTitle()
    {
        Assert.Null(Pick("Lucid Dreams (Remix)", "Juice WRLD", Hit(2, "Lucid Dreams", "Juice WRLD")));
    }

    [Theory]
    [InlineData("unreleased")]
    [InlineData("")]
    public void LyricsNotComplete_IsRejected(string state)
    {
        Assert.Null(Pick("Song", "Artist", Hit(1, "Song", "Artist", state)));
    }

    [Fact]
    public void InstrumentalHit_IsRejected()
    {
        Assert.Null(Pick("Song", "Artist", Hit(1, "Song", "Artist", instrumental: true)));
    }

    [Fact]
    public void UpperCaseGeniusTitle_MatchesOurTitleWithAFeaturedArtist()
    {
        var picked = Pick("Mannschaft (feat. THIZZY52)", "JoeyAK", Hit(7, "MANNSCHAFT", "JoeyAK"));

        Assert.Equal(7, picked?.Id);
    }

    [Fact]
    public void CombinedCredit_MatchesThroughItsPrimaryArtist()
    {
        var picked = Pick("MANNSCHAFT", "JoeyAK, THIZZY52", Hit(7, "MANNSCHAFT", "JoeyAK"));

        Assert.Equal(7, picked?.Id);
    }

    [Fact]
    public void DifferentSongBySameArtist_IsRejected()
    {
        Assert.Null(Pick("MANNSCHAFT", "JoeyAK", Hit(1, "ZUCKERBROT", "JoeyAK")));
    }

    [Fact]
    public void SameTitleByAnotherArtist_IsRejected()
    {
        Assert.Null(Pick("Titanium", "Some Local Band", Hit(1, "Titanium", "David Guetta")));
    }

    [Fact]
    public void ArtistNamedInsideACombinedCredit_IsRejected()
    {
        Assert.Null(Pick("On Sight", "Fenix Flexin, Purps On The Beat", Hit(1, "On Sight", "The Beat")));
    }

    [Fact]
    public void ShortNameInsideALongerArtistName_IsRejected()
    {
        Assert.Null(Pick("Waves", "Joey", Hit(1, "Waves", "Joey Bada$$")));
    }

    [Fact]
    public void GenericTitleWithoutAKnownYear_IsRejected()
    {
        Assert.Null(Pick("Intro", "Kanye West", Hit(120581, "Intro", "Kanye West")));
    }

    [Fact]
    public void FirstAcceptableHitInGeniusOrderWins()
    {
        var picked = Pick("Song", "Artist", Hit(1, "Song", "Artist"), Hit(2, "Song", "Artist"));

        Assert.Equal(1, picked?.Id);
    }

    [Fact]
    public void MissingTitleOrArtist_PicksNothing()
    {
        Assert.Null(GeniusHitMatcher.Pick([Hit(1, "Song", "Artist")], null, "Artist", Threshold, Threshold));
        Assert.Null(GeniusHitMatcher.Pick([Hit(1, "Song", "Artist")], "Song", " ", Threshold, Threshold));
    }
}
