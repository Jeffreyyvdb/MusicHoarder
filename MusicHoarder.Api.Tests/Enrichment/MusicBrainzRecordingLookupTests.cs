using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

public class MusicBrainzRecordingLookupTests
{
    [Fact]
    public async Task LookupByRecordingId_MapsDescriptiveMetadata()
    {
        // A recording payload carrying every descriptive field the mapper reads: genres, label-info
        // (label + catalog-number), release barcode, release-group first-release-date, and artist
        // sort-names.
        var handler = new StubHandler("""
            {
              "id": "rec-1",
              "title": "N.Y. State of Mind",
              "length": 293000,
              "isrcs": ["USxxx0000001"],
              "artist-credit": [
                { "name": "Nas", "joinphrase": "",
                  "artist": { "id": "a1", "name": "Nas", "sort-name": "Nas" } }
              ],
              "genres": [
                { "name": "hip hop", "count": 9 },
                { "name": "east coast hip hop", "count": 4 }
              ],
              "releases": [
                {
                  "id": "rel-1",
                  "title": "Illmatic",
                  "date": "1994-04-19",
                  "barcode": "074321208472",
                  "label-info": [
                    { "catalog-number": "COL 481290 2", "label": { "name": "Columbia" } }
                  ],
                  "release-group": {
                    "id": "rg-1",
                    "primary-type": "Album",
                    "secondary-types": [],
                    "first-release-date": "1994-04-19"
                  },
                  "media": [ { "position": 1, "track-count": 10 } ]
                }
              ]
            }
            """);

        var rec = await CreateService(handler).LookupByRecordingIdAsync("rec-1");

        Assert.NotNull(rec);
        Assert.Equal("Hip Hop; East Coast Hip Hop", rec!.Genre); // highest count first, Title Cased
        Assert.Equal("1994-04-19", rec.ReleaseDate);
        Assert.Equal("1994-04-19", rec.OriginalReleaseDate);
        Assert.Equal("Columbia", rec.Label);
        Assert.Equal("COL 481290 2", rec.CatalogNumber);
        Assert.Equal("074321208472", rec.Barcode);
        Assert.Equal("Nas", rec.ArtistSort);
        Assert.Equal("Nas", rec.AlbumArtistSort);
    }

    [Fact]
    public async Task LookupByRecordingId_FallsBackToTagsForGenre_AndBuildsMultiArtistSort()
    {
        var handler = new StubHandler("""
            {
              "id": "rec-2",
              "title": "Song",
              "artist-credit": [
                { "name": "The Beatles", "joinphrase": " feat. ",
                  "artist": { "id": "a1", "name": "The Beatles", "sort-name": "Beatles, The" } },
                { "name": "Billy Preston", "joinphrase": "",
                  "artist": { "id": "a2", "name": "Billy Preston", "sort-name": "Preston, Billy" } }
              ],
              "genres": [],
              "tags": [ { "name": "rock", "count": 3 } ],
              "releases": []
            }
            """);

        var rec = await CreateService(handler).LookupByRecordingIdAsync("rec-2");

        Assert.NotNull(rec);
        Assert.Equal("Rock", rec!.Genre); // fell back to tags
        Assert.Equal("Beatles, The feat. Preston, Billy", rec.ArtistSort);
        Assert.Equal("Beatles, The", rec.AlbumArtistSort); // primary credit's sort-name
    }

    [Fact]
    public async Task LookupByIsrc_ResolvesTheRecording_ThenLooksItUpWithItsReleases()
    {
        // The isrc resource answers 400 to any inc it does not know (release-groups, genres, labels)
        // and leaves releases out even when asked, so the release data comes from a recording lookup.
        var handler = new RoutingHandler(new()
        {
            ["/ws/2/isrc/USXXX0000001?fmt=json"] = """
                { "isrc": "USXXX0000001", "recordings": [ { "id": "rec-1", "title": "N.Y. State of Mind" }, { "id": "rec-2" } ] }
                """,
            ["/ws/2/recording/rec-1?inc=artist-credits+releases+release-groups+media+isrcs+genres&fmt=json"] = """
                {
                  "id": "rec-1",
                  "title": "N.Y. State of Mind",
                  "artist-credit": [ { "name": "Nas", "joinphrase": "", "artist": { "id": "a1", "name": "Nas" } } ],
                  "releases": [ { "id": "rel-1", "title": "Illmatic", "date": "1994-04-19",
                    "release-group": { "id": "rg-1", "primary-type": "Album" } } ]
                }
                """,
        });

        var rec = await CreateService(handler).LookupByIsrcAsync(" us-xxx-00-00001 ");

        Assert.NotNull(rec);
        Assert.Equal("rel-1", rec!.ReleaseId);
        Assert.Equal("Illmatic", rec.ReleaseTitle);
        Assert.Equal("rg-1", rec.ReleaseGroupId);
        Assert.Equal("USXXX0000001", rec.Isrc);
        Assert.Equal(2, rec.CandidateCount);
    }

    private static MusicBrainzWebService CreateService(HttpMessageHandler handler)
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

    private sealed class RoutingHandler(Dictionary<string, string> bodiesByPathAndQuery) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(bodiesByPathAndQuery.TryGetValue(request.RequestUri!.PathAndQuery, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest));
    }
}
