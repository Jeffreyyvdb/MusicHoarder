using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Pins how an AcoustID lookup response becomes an <see cref="AcoustIdMatch"/>'s artist fields — in
/// particular that the album-artist is the first structured artist, not a re-parse of the display join.
/// </summary>
public class AcoustIdServiceTests
{
    [Theory]
    [InlineData("Simon & Garfunkel")]
    [InlineData("Tyler, The Creator")]
    public async Task Lookup_SingleArtistWhoseNameHoldsADelimiter_AlbumArtistIsNotTruncated(string name)
    {
        var match = await LookupAsync($$"""[ { "id": "mbid-1", "name": "{{name}}" } ]""");

        Assert.NotNull(match);
        // Re-parsing the display credit cut these to "Simon" / "Tyler".
        Assert.Equal(name, match.AlbumArtist);
        Assert.Equal(name, match.Artist);
    }

    [Fact]
    public async Task Lookup_SeveralArtists_AlbumArtistIsTheFirst()
    {
        var match = await LookupAsync("""
            [ { "id": "mbid-hef", "name": "Hef" }, { "id": "mbid-jayh", "name": "Jayh" } ]
            """);

        Assert.NotNull(match);
        Assert.Equal("Hef", match.AlbumArtist);
        Assert.Equal("Hef; Jayh", match.Artist);
        Assert.Equal("Hef; Jayh", match.Artists);
        Assert.Equal("mbid-hef; mbid-jayh", match.ArtistMusicBrainzIds);
    }

    [Fact]
    public async Task Lookup_NoArtists_AlbumArtistIsEmpty()
    {
        var match = await LookupAsync("[]");

        Assert.NotNull(match);
        Assert.Equal(string.Empty, match.AlbumArtist);
        Assert.Equal(string.Empty, match.Artist);
    }

    private static Task<AcoustIdMatch?> LookupAsync(string artistsJson)
    {
        var json = $$"""
            {
              "status": "ok",
              "results": [
                {
                  "id": "acoustid-1",
                  "score": 0.97,
                  "recordings": [
                    { "id": "rec-1", "title": "Song", "duration": 200, "artists": {{artistsJson}} }
                  ]
                }
              ]
            }
            """;
        var httpClient = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://api.acoustid.org/") };
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            AcoustIdApiKey = "test-key",
        });
        return new AcoustIdService(httpClient, options, NullLogger<AcoustIdService>.Instance).LookupAsync("fp", 200);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
