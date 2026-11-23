using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

public class GeniusHitMatcherTests
{
    private const double Threshold = 85;

    private static GeniusHit Hit(
        int id, string title, string artist, string state = "complete", bool instrumental = false, int? year = null) =>
        new(id, title, artist, state, instrumental, year);

    private static GeniusHit? Pick(string title, string artist, params GeniusHit[] hits) =>
        PickIn(null, title, artist, hits);

    private static GeniusHit? PickIn(int? year, string title, string artist, params GeniusHit[] hits) =>
        GeniusHitMatcher.Pick(hits, title, artist, year, Threshold, Threshold);

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

    [Theory]
    [InlineData("Beyonce", "Beyoncé")]
    [InlineData("¥$", "¥$")]
    public void SpellingVariantsOfOneArtist_Match(string ours, string theirs)
    {
        Assert.Equal(1, Pick("Song", ours, Hit(1, "Song", theirs))?.Id);
    }

    [Fact]
    public void HitWithNoArtist_IsRejected()
    {
        Assert.Null(Pick("Song", "Artist", Hit(1, "Song", "")));
    }

    [Theory]
    [InlineData("P!nk", "!")]
    [InlineData("Ke$ha", "$")]
    [InlineData("$$", "Ke$$a")]
    [InlineData("AC/DC", "/")]
    public void SymbolsFromInsideAName_AreNotThatArtist(string ours, string theirs)
    {
        Assert.Null(Pick("Song", ours, Hit(1, "Song", theirs)));
    }

    [Theory]
    [InlineData("The Intro")]
    [InlineData("Interlude II")]
    [InlineData("Intro Pt. 2")]
    [InlineData("(Intro)")]
    public void GenericTitleSpellings_AlsoNeedTheSameYear(string title)
    {
        Assert.Null(PickIn(2001, title, "Artist", Hit(1, title, "Artist", year: 1994)));
    }

    [Theory]
    [InlineData("Interlude", "Interlude 2")]
    [InlineData("Skit (1)", "Skit (2)")]
    [InlineData("Skit [1]", "Skit [2]")]
    [InlineData("Intro (Part 2)", "Intro (Part 1)")]
    [InlineData("Intro", "Intro (Part 2)")]
    public void GenericTitle_NeedsTheSameNumberAsTheHit(string ours, string theirs)
    {
        Assert.Null(PickIn(2004, ours, "Artist", Hit(1, theirs, "Artist", year: 2004)));
    }

    [Theory]
    [InlineData("Intro")]
    [InlineData("Interlude 2")]
    [InlineData("Interlude II")]
    [InlineData("(Intro)")]
    [InlineData("Skit (1)")]
    [InlineData("Intro (Part 2)")]
    [InlineData("Skit #3")]
    [InlineData("Outro (feat. Someone)")]
    public void GenericTitle_TakesTheHitFromTheSameYear(string title)
    {
        var picked = PickIn(2004, title, "Kanye West",
            Hit(1, title, "Kanye West", year: 2010),
            Hit(2, title, "Kanye West"),
            Hit(3, title, "Kanye West", year: 2004));

        Assert.Equal(3, picked?.Id);
    }

    [Fact]
    public void GenericTitle_IsRejectedWhenTheSongsYearIsUnknown()
    {
        Assert.Null(PickIn(null, "Intro", "Kanye West", Hit(1, "Intro", "Kanye West", year: 2004)));
    }

    [Fact]
    public void TitleWithAGenericWordAndMore_NeedsNoYear()
    {
        Assert.Equal(1, Pick("Intro to the Night", "Artist", Hit(1, "Intro to the Night", "Artist", year: 1999))?.Id);
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
        Assert.Null(GeniusHitMatcher.Pick([Hit(1, "Song", "Artist")], null, "Artist", null, Threshold, Threshold));
        Assert.Null(GeniusHitMatcher.Pick([Hit(1, "Song", "Artist")], "Song", " ", null, Threshold, Threshold));
    }
}
