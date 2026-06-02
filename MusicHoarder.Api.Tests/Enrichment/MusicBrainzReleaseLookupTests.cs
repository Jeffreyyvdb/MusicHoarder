using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

public class MusicBrainzReleaseLookupTests
{
    [Fact]
    public async Task LookupReleaseAsync_MapsMediaTracksRecordingsAndTotals()
    {
        var handler = new StubHandler("""
            {
              "id": "rel-1",
              "title": "Discovery",
              "date": "2001-03-12",
              "artist-credit": [ { "name": "Daft Punk", "artist": { "id": "a1", "name": "Daft Punk" } } ],
              "media": [
                {
                  "position": 1,
                  "track-count": 2,
                  "tracks": [
                    { "position": 1, "number": "1", "title": "One More Time", "length": 320000,
                      "recording": { "id": "rec-1", "title": "One More Time", "length": 320000 } },
                    { "position": 2, "number": "2", "title": null, "length": null,
                      "recording": { "id": "rec-2", "title": "Aerodynamic", "length": 210000 } }
                  ]
                }
              ]
            }
            """);

        var release = await CreateService(handler).LookupReleaseAsync("rel-1");

        Assert.NotNull(release);
        Assert.Equal("rel-1", release!.Id);
        Assert.Equal("Discovery", release.Title);
        Assert.Equal("Daft Punk", release.AlbumArtist);
        Assert.Equal(2001, release.Year);
        Assert.Equal(1, release.TotalDiscs);
        Assert.Equal(2, release.TotalTracks);

        Assert.Equal(2, release.Tracks.Count);

        var first = release.Tracks[0];
        Assert.Equal(1, first.DiscNumber);
        Assert.Equal(1, first.TrackNumber);
        Assert.Equal("One More Time", first.Title);
        Assert.Equal(320000, first.LengthMs);
        Assert.Equal("rec-1", first.RecordingId);

        // Track title/length fall back to the nested recording when absent on the track.
        var second = release.Tracks[1];
        Assert.Equal(2, second.TrackNumber);
        Assert.Equal("Aerodynamic", second.Title);
        Assert.Equal(210000, second.LengthMs);
        Assert.Equal("rec-2", second.RecordingId);
    }

    [Fact]
    public async Task LookupReleaseAsync_FlattensMultipleDiscs()
    {
        var handler = new StubHandler("""
            {
              "id": "rel-2",
              "title": "The Wall",
              "media": [
                { "position": 1, "track-count": 1, "tracks": [ { "position": 1, "title": "In the Flesh?" } ] },
                { "position": 2, "track-count": 1, "tracks": [ { "position": 1, "title": "Hey You" } ] }
              ]
            }
            """);

        var release = await CreateService(handler).LookupReleaseAsync("rel-2");

        Assert.NotNull(release);
        Assert.Equal(2, release!.TotalDiscs);
        Assert.Equal(2, release.TotalTracks);
        Assert.Collection(release.Tracks,
            t => Assert.Equal((1, 1), (t.DiscNumber, t.TrackNumber)),
            t => Assert.Equal((2, 1), (t.DiscNumber, t.TrackNumber)));
    }

    [Fact]
    public async Task LookupReleaseAsync_NotFound_ReturnsNull()
    {
        var handler = new StubHandler("", HttpStatusCode.NotFound);
        var release = await CreateService(handler).LookupReleaseAsync("missing");
        Assert.Null(release);
    }

    private static MusicBrainzWebService CreateService(StubHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://musicbrainz.org/ws/2/"),
        };
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        });
        return new MusicBrainzWebService(httpClient, options, NullLogger<MusicBrainzWebService>.Instance);
    }

    private sealed class StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
