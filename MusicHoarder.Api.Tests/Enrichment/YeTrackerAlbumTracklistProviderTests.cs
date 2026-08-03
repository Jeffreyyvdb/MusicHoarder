using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

public class YeTrackerAlbumTracklistProviderTests
{
    [Fact]
    public async Task ResolvesAnUnreleasedAlbumByName()
    {
        var provider = Create(Tracklist("Yandhi", "Yandhi [V1]",
            [(1, "Alien"), (2, "Chakras"), (3, "Hurricane")], year: 2018));

        var result = await provider.FetchAsync(Query("Kanye West", "Yandhi"));

        Assert.NotNull(result);
        Assert.Equal("Yandhi", result!.Title);
        Assert.Equal(2018, result.Year);
        Assert.Equal(3, result.Tracks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.Tracks.Select(t => t.TrackNumber));
        Assert.Equal("Chakras", result.Tracks[1].Title);
    }

    [Fact]
    public async Task FallsBackToTheEra_BecauseLeaksAreFiledUnderIt()
    {
        var provider = Create(Tracklist("Good Ass Job [V2]", "Good Ass Job",
            [(1, "Power"), (2, "Devil In A New Dress")]));

        var result = await provider.FetchAsync(Query("Kanye West", "Good Ass Job"));

        Assert.NotNull(result);
        Assert.Equal("Good Ass Job [V2]", result!.Title);
    }

    [Fact]
    public async Task SetlistsAreNotTreatedAsAlbums()
    {
        var provider = Create(Tracklist("VULTURES 2 [China Setlist #1]", "VULTURES 2",
            [(1, "FRIED"), (2, "STARS")], isSetlist: true));

        Assert.Null(await provider.FetchAsync(Query("Kanye West", "VULTURES 2 [China Setlist #1]")));
    }

    [Fact]
    public async Task AnotherArtistsAlbum_IsNotAnswered()
    {
        // The catalog is single-artist; answering for anyone else would attach Ye's running order
        // to somebody else's record.
        var provider = Create(Tracklist("Yandhi", "Yandhi", [(1, "Alien"), (2, "Chakras")]));

        Assert.Null(await provider.FetchAsync(Query("Taylor Swift", "Yandhi")));
    }

    [Theory]
    [InlineData("Yeat")]
    [InlineData("Yebba")]
    [InlineData("Yeah Yeah Yeahs")]
    public async Task ArtistMerelyContainingYe_IsNotAnswered(string albumArtist)
    {
        // Same trap as the enrichment gate: a plain fuzzy ratio scores 90 for the two-letter "Ye"
        // alias against any name containing those letters, which would staple Ye's running order
        // onto an unrelated artist's album.
        var provider = Create(Tracklist("Yandhi", "Yandhi", [(1, "Alien"), (2, "Chakras")]));

        Assert.Null(await provider.FetchAsync(Query(albumArtist, "Yandhi")));
    }

    [Fact]
    public async Task KanyeCollaboration_StillOpensTheGate()
    {
        var provider = Create(Tracklist("Yandhi", "Yandhi", [(1, "Alien"), (2, "Chakras")]));

        Assert.NotNull(await provider.FetchAsync(Query("Ye, Ty Dolla $ign", "Yandhi")));
    }

    [Fact]
    public async Task UnknownAlbum_ReturnsNull()
    {
        var provider = Create(Tracklist("Yandhi", "Yandhi", [(1, "Alien"), (2, "Chakras")]));

        Assert.Null(await provider.FetchAsync(Query("Kanye West", "Bohemian Rhapsody")));
    }

    [Fact]
    public void IsEnabled_TracksTheYeTrackerFlag()
    {
        var provider = Create();
        Assert.True(provider.IsEnabled(new MusicEnricherOptions { EnableYeTrackerProvider = true }));
        Assert.False(provider.IsEnabled(new MusicEnricherOptions { EnableYeTrackerProvider = false }));
    }

    // --- helpers ---

    private static YeTrackerAlbumTracklistProvider Create(params TrackerTracklist[] tracklists)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            EnableYeTrackerProvider = true,
        });
        return new YeTrackerAlbumTracklistProvider(new YeTrackerTracklistCatalogService(tracklists), options);
    }

    private static TrackerTracklist Tracklist(
        string album,
        string? era,
        (int Number, string Title)[] tracks,
        int? year = null,
        bool isSetlist = false) =>
        new(album, era, year, "Clear",
            tracks.Select(t => new TrackerTracklistEntry(t.Number, t.Title)).ToList(),
            isSetlist);

    private static AlbumQuery Query(string albumArtist, string album) =>
        new(albumArtist, album, null, null, [], null);
}
