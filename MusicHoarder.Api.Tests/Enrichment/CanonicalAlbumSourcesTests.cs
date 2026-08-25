using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class CanonicalAlbumSourcesTests
{
    private static AlbumTracklistReconciler.ReconciledSource Source(
        EnrichmentProvider provider, string? albumId, int trackCount, bool winning) =>
        new(provider, albumId, trackCount, winning);

    [Fact]
    public void SerializeThenParse_RoundTripsEveryField()
    {
        var json = CanonicalAlbumSources.Serialize(
        [
            Source(EnrichmentProvider.MusicBrainzWeb, "rel-1", 12, winning: true),
            Source(EnrichmentProvider.Deezer, null, 11, winning: false),
        ]);

        var parsed = CanonicalAlbumSources.Parse(json);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(EnrichmentProvider.MusicBrainzWeb, parsed[0].Provider);
        Assert.Equal("rel-1", parsed[0].AlbumId);
        Assert.Equal(12, parsed[0].TrackCount);
        Assert.True(parsed[0].InWinningCluster);
        Assert.Null(parsed[1].AlbumId);
        Assert.False(parsed[1].InWinningCluster);
    }

    [Fact]
    public void Parse_ReadsThePersistedWireFormat()
    {
        // The historical on-disk shape: PascalCase names, numeric enum values.
        var parsed = CanonicalAlbumSources.Parse(
            """[{"Provider":2,"AlbumId":"rel-1","TrackCount":4,"InWinningCluster":true}]""");

        var only = Assert.Single(parsed);
        Assert.Equal((EnrichmentProvider)2, only.Provider);
        Assert.Equal("rel-1", only.AlbumId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    public void Parse_EmptyOrMalformedPayload_ReadsAsNoSources(string? json)
    {
        Assert.Empty(CanonicalAlbumSources.Parse(json));
    }

    [Fact]
    public void WinningProviderNames_ReturnsDistinctWinnersOnly()
    {
        var json = CanonicalAlbumSources.Serialize(
        [
            Source(EnrichmentProvider.MusicBrainzWeb, "a", 10, winning: true),
            Source(EnrichmentProvider.MusicBrainzWeb, "b", 10, winning: true),
            Source(EnrichmentProvider.Deezer, "c", 10, winning: true),
            Source(EnrichmentProvider.SpotifyAPI, "d", 9, winning: false),
        ]);

        Assert.Equal(["MusicBrainzWeb", "Deezer"], CanonicalAlbumSources.WinningProviderNames(json));
    }
}
