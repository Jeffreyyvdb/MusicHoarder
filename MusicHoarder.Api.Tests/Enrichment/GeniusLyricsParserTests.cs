using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

public class GeniusLyricsParserTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Genius", "joeyak-mannschaft.html"));

    [Fact]
    public void RealPage_YieldsTheWordsWithoutPageChromeOrSectionLabels()
    {
        var lyrics = GeniusLyricsParser.Parse(Fixture());

        Assert.NotNull(lyrics);
        Assert.StartsWith("Scheiße", lyrics);
        Assert.DoesNotContain("Contributors", lyrics);
        Assert.DoesNotContain("Lyrics", lyrics.Split('\n')[0]);
        Assert.DoesNotContain("[Chorus", lyrics);
        Assert.DoesNotContain("[Intro", lyrics);
        Assert.DoesNotContain("Songtekst van", lyrics);
        Assert.DoesNotContain("<", lyrics);
        Assert.DoesNotContain("&amp;", lyrics);
        Assert.DoesNotContain("&#x27;", lyrics);
        Assert.Contains("Glock, AK's, Heckler & Koch, die jongens geven geen fuck", lyrics);
        Assert.DoesNotContain("\n\n\n", lyrics);
    }

    [Fact]
    public void Containers_AreJoinedAndLineBreaksBecomeNewlines()
    {
        const string html = """
            <div data-lyrics-container="true" class="a">One<br/>Two<br>Three</div>
            <p>unrelated</p>
            <div data-lyrics-container="true">Four <i>five</i> <a href="/x"><span>six</span></a></div>
            """;

        Assert.Equal("One\nTwo\nThree\nFour five six", GeniusLyricsParser.Parse(html));
    }

    [Fact]
    public void ExcludedSubtree_IsDroppedEvenWhenItNestsTheSameTag()
    {
        const string html = """
            <div data-lyrics-container="true"><div data-exclude-from-selection="true"><div>2 Contributors</div><div>Title Lyrics</div></div>Real line<br/>Next</div>
            """;

        Assert.Equal("Real line\nNext", GeniusLyricsParser.Parse(html));
    }

    [Fact]
    public void SectionLabelLines_AreRemovedButBracketsInsideALineStay()
    {
        const string html =
            """<div data-lyrics-container="true">[Verse 1: A, B]<br/>Hello [yeah] world<br/><br/><br/><br/>[Hook]<br/>Bye</div>""";

        Assert.Equal("Hello [yeah] world\n\nBye", GeniusLyricsParser.Parse(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>Challenge page</body></html>")]
    [InlineData("""<div data-lyrics-container="true">[Intro]<br/>  </div>""")]
    public void NoContainerOrNothingLeft_IsNull(string? html)
    {
        Assert.Null(GeniusLyricsParser.Parse(html));
    }
}
