using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

public class YeTrackerCatalogServiceTests
{
    private static YeTrackerCatalogService Catalog(int searchLimit, params TrackerSong[] songs) =>
        new(songs, Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            TrackerSearchLimit = searchLimit,
        }));

    private static TrackerSong Song(string name, params string[] aliases) =>
        new(0, name, aliases, "unreleased", "Donda", "Kanye West", null, null, null);

    [Fact]
    public async Task SearchAsync_MatchesOnCanonicalName()
    {
        var catalog = Catalog(20, Song("Famous"), Song("Wolves"));

        var results = await catalog.SearchAsync("Famous");

        Assert.Contains(results, r => r.Name == "Famous");
    }

    [Fact]
    public async Task SearchAsync_MatchesOnAlias_IgnoringVersionMarkers()
    {
        var catalog = Catalog(20, Song("Wolves [V2]", "Wolves"));

        var results = await catalog.SearchAsync("Wolves");

        Assert.Contains(results, r => r.Name == "Wolves [V2]");
    }

    [Fact]
    public async Task SearchAsync_RespectsSearchLimit()
    {
        var catalog = Catalog(2,
            Song("Love 1"), Song("Love 2"), Song("Love 3"), Song("Love 4"), Song("Love 5"));

        var results = await catalog.SearchAsync("Love");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty()
    {
        var catalog = Catalog(20, Song("Famous"));

        Assert.Empty(await catalog.SearchAsync(""));
        Assert.Empty(await catalog.SearchAsync("   "));
    }

    [Fact]
    public async Task SearchAsync_NoMatch_ReturnsEmpty()
    {
        var catalog = Catalog(20, Song("Famous"), Song("Wolves"));

        Assert.Empty(await catalog.SearchAsync("Bohemian Rhapsody"));
    }

    [Fact]
    public async Task SearchAsync_MatchesOnOgFilename()
    {
        // An untagged leak is usually named after the OG filename and nothing else, so that name has
        // to reach the coarse filter — otherwise the candidate is never scored at all.
        var catalog = Catalog(20, Song("Diamonds [V4]") with { OgFilenames = ["DIAMONDSAD122_01"] });

        var results = await catalog.SearchAsync("DIAMONDSAD122_01");

        Assert.Contains(results, r => r.Name == "Diamonds [V4]");
    }
}
