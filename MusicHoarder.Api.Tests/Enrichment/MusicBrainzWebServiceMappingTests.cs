using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Pins the MusicBrainz response-mapping rules (release types, artist credits, genre ranking,
/// ISRC lookup semantics, release search projection) at the service's public surface, so the
/// mapping can be restructured without changing observable behaviour.
/// </summary>
public class MusicBrainzWebServiceMappingTests
{
    [Fact]
    public async Task Search_MapsReleaseTypes_AndCompilationFlag()
    {
        var handler = new StubHandler("""
            {
              "recordings": [
                {
                  "id": "rec-1",
                  "title": "Song",
                  "score": 97,
                  "artist-credit": [ { "name": "Artist", "artist": { "id": "a1", "name": "Artist" } } ],
                  "releases": [
                    {
                      "id": "rel-1",
                      "title": "Greatest Hits",
                      "release-group": {
                        "id": "rg-1",
                        "primary-type": "Album",
                        "secondary-types": ["Compilation", "Live"]
                      },
                      "media": [ { "position": 1, "track-count": 12 } ]
                    }
                  ]
                }
              ]
            }
            """);

        var results = await CreateService(handler).SearchAsync("Artist", "Song", 5);

        var rec = Assert.Single(results);
        Assert.Equal(97, rec.Score);
        Assert.Equal("album", rec.ReleaseTypePrimary);
        Assert.Equal("album; compilation; live", rec.ReleaseTypes);
        Assert.True(rec.IsCompilation);
        Assert.Equal("rg-1", rec.ReleaseGroupId);
        Assert.Equal(1, rec.TotalDiscs);
        Assert.Equal(12, rec.TotalTracks);
    }

    [Fact]
    public async Task Search_WithoutPrimaryType_JoinsSecondaryTypesOnly()
    {
        var handler = new StubHandler("""
            {
              "recordings": [
                {
                  "id": "rec-1",
                  "title": "Song",
                  "artist-credit": [ { "name": "Artist", "artist": { "id": "a1", "name": "Artist" } } ],
                  "releases": [
                    {
                      "id": "rel-1",
                      "title": "Live at Wembley",
                      "release-group": { "id": "rg-1", "secondary-types": ["Live"] },
                      "media": [ { "position": 1, "track-count": 0 } ]
                    }
                  ]
                }
              ]
            }
            """);

        var results = await CreateService(handler).SearchAsync("Artist", "Song", 5);

        var rec = Assert.Single(results);
        Assert.Null(rec.ReleaseTypePrimary);
        Assert.Equal("live", rec.ReleaseTypes);
        Assert.False(rec.IsCompilation);
        // A media list whose track counts sum to zero must not claim a total of 0 tracks.
        Assert.Equal(1, rec.TotalDiscs);
        Assert.Null(rec.TotalTracks);
    }

    [Fact]
    public async Task Search_BuildsDisplayCredit_DiscreteArtists_AndAlignedIds()
    {
        var handler = new StubHandler("""
            {
              "recordings": [
                {
                  "id": "rec-1",
                  "title": "Collab",
                  "artist-credit": [
                    { "name": "Alpha", "joinphrase": " & ",
                      "artist": { "id": "mbid-alpha", "name": "Alpha" } },
                    { "name": "Beta (credited)", "joinphrase": "",
                      "artist": { "id": "mbid-beta", "name": "Beta" } }
                  ],
                  "releases": []
                }
              ]
            }
            """);

        var results = await CreateService(handler).SearchAsync("Alpha", "Collab", 5);

        var rec = Assert.Single(results);
        // Display credit uses the credited-as names plus join phrases.
        Assert.Equal("Alpha & Beta (credited)", rec.Artist);
        Assert.Equal("Alpha", rec.AlbumArtist);
        // Discrete artists prefer the canonical artist name over the credited-as spelling.
        Assert.Equal("Alpha; Beta", rec.Artists);
        Assert.Equal("mbid-alpha; mbid-beta", rec.ArtistMusicBrainzIds);
        Assert.Equal("mbid-alpha", rec.AlbumArtistMusicBrainzId);
    }

    [Fact]
    public async Task LookupByRecordingId_RanksGenresByCount_Dedupes_AndCapsAtFive()
    {
        var handler = new StubHandler("""
            {
              "id": "rec-1",
              "title": "Song",
              "artist-credit": [ { "name": "Artist", "artist": { "id": "a1", "name": "Artist" } } ],
              "genres": [
                { "name": "rock", "count": 1 },
                { "name": "pop", "count": 9 },
                { "name": "POP", "count": 8 },
                { "name": "jazz", "count": 7 },
                { "name": "blues", "count": 6 },
                { "name": "soul", "count": 5 },
                { "name": "funk", "count": 4 }
              ],
              "releases": []
            }
            """);

        var rec = await CreateService(handler).LookupByRecordingIdAsync("rec-1");

        Assert.NotNull(rec);
        Assert.Equal("Pop; Jazz; Blues; Soul; Funk", rec!.Genre);
    }

    [Fact]
    public async Task LookupByIsrc_SetsCandidateCount_AndNormalizedIsrc()
    {
        var handler = new StubHandler("""
            {
              "recordings": [
                {
                  "id": "rec-1",
                  "title": "First Match",
                  "isrcs": ["USAAA0000001"],
                  "artist-credit": [ { "name": "Artist", "artist": { "id": "a1", "name": "Artist" } } ],
                  "releases": []
                },
                {
                  "id": "rec-2",
                  "title": "Second Match",
                  "artist-credit": [ { "name": "Artist", "artist": { "id": "a1", "name": "Artist" } } ],
                  "releases": []
                }
              ]
            }
            """);

        var rec = await CreateService(handler).LookupByIsrcAsync(" us-aaa-0000001 ");

        Assert.NotNull(rec);
        Assert.Equal("rec-1", rec!.Id);
        Assert.Equal(2, rec.CandidateCount);
        // The queried ISRC (trimmed, uppercased, dashes stripped) wins over whatever the recording carries.
        Assert.Equal("USAAA0000001", rec.Isrc);
    }

    [Fact]
    public async Task SearchReleases_FiltersBlankIds_AndMapsFields()
    {
        var handler = new StubHandler("""
            {
              "releases": [
                { "id": "", "title": "Ghost Entry", "date": "1999", "track-count": 9, "score": 100 },
                { "id": "rel-1", "title": "Kept", "date": "2001-03-12", "track-count": 14, "score": 92 },
                { "id": "rel-2", "title": "No Score" }
              ]
            }
            """);

        var results = await CreateService(handler).SearchReleasesAsync("Artist", "Kept", 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("rel-1", results[0].Id);
        Assert.Equal("Kept", results[0].Title);
        Assert.Equal(2001, results[0].Year);
        Assert.Equal(14, results[0].TrackCount);
        Assert.Equal(92, results[0].Score);
        // Missing score defaults to 0 rather than null.
        Assert.Equal(0, results[1].Score);
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
