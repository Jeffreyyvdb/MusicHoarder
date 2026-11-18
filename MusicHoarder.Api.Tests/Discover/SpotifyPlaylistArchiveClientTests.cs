using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Discover;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Discover;

public class SpotifyPlaylistArchiveClientTests
{
    private const string Old = "https://example.test/old";
    private const string New = "https://example.test/new";
    private const string IdA = "37i9dQZF1DXcBWIGoYBM5M";
    private const string IdB = "37i9dQZF1DX0XUsuxWHRQd";

    private static byte[] Brotli(string json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    private static string Metadata(params (string Id, string Name, long Followers)[] entries) =>
        "{" + string.Join(",", entries.Select(e =>
            $$"""
            "{{e.Id}}": { "description": "", "num_followers": {{e.Followers}}, "original_name": "{{e.Name}}", "owner": { "name": "Spotify" }, "snapshot_id": "snap", "unique_name": "{{e.Name}}" }
            """)) + "}";

    private static string TracksFile(string title) =>
        $$"""{ "tracks": [ { "added_at": "2026-11-06 05:02:41", "artists": [ { "name": "Ann" } ], "duration_ms": 1000, "name": "{{title}}", "url": "https://open.spotify.com/track/3h5T5JypYU7huFiVYhv1dr" } ] }""";

    private sealed class Stub : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];

        public Stub Metadata(string baseUrl, string json)
        {
            Routes[$"{baseUrl}/playlists/metadata/metadata-full.json.br"] =
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Brotli(json)) };
            return this;
        }

        public Stub Tracks(string baseUrl, string id, string json)
        {
            Routes[$"{baseUrl}/playlists/pretty/{id}.json"] =
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static SpotifyPlaylistArchiveClient CreateClient(Stub stub, params string[] sources) =>
        new(new HttpClient(stub),
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/s",
                DestinationDirectory = "/d",
                SpotifyPlaylistArchiveSources = sources,
            }),
            NullLogger<SpotifyPlaylistArchiveClient>.Instance);

    [Fact]
    public async Task Browse_MergesTheSources_ALaterOneWins_AndAnOlderOnlyPlaylistStillShows()
    {
        var stub = new Stub()
            .Metadata(Old, Metadata((IdA, "Old name", 1), (IdB, "Only old", 5)))
            .Metadata(New, Metadata((IdA, "New name", 9)));

        var playlists = await CreateClient(stub, Old, New).BrowseAsync(null, 10, default);

        Assert.Equal([("New name", New), ("Only old", Old)], playlists!.Select(p => (p.Name, p.SourceBaseUrl)));
    }

    [Fact]
    public async Task Browse_AFailingSourceIsSkipped_AndTheOthersStillMerge()
    {
        var stub = new Stub().Metadata(Old, Metadata((IdB, "Only old", 5)));

        var playlists = await CreateClient(stub, Old, New).BrowseAsync(null, 10, default);

        Assert.Equal(["Only old"], playlists!.Select(p => p.Name));
    }

    [Fact]
    public async Task Browse_EverySourceFailing_IsNull_AndIsNotRetriedWithinTheMinute()
    {
        var stub = new Stub();
        var client = CreateClient(stub, Old, New);

        Assert.Null(await client.BrowseAsync(null, 10, default));
        var requests = stub.Requests.Count;
        Assert.Null(await client.BrowseAsync("rap", 10, default));

        Assert.Equal(requests, stub.Requests.Count);
    }

    [Fact]
    public async Task Browse_DownloadsTheCatalogOnce_ForConcurrentRequests()
    {
        var stub = new Stub().Metadata(Old, Metadata((IdA, "A", 1)));
        var client = CreateClient(stub, Old);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.BrowseAsync(null, 10, default)));

        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("37i9dQZF1DXcBWIGoYBM5")]
    [InlineData("")]
    public async Task AnIdThatIsNotASpotifyId_IsNeverFetched(string id)
    {
        var stub = new Stub().Metadata(Old, Metadata((IdA, "A", 1)));
        var client = CreateClient(stub, Old);

        Assert.Null(await client.FindAsync(id, default));
        Assert.Null(await client.GetTracksAsync(id, default));
        Assert.Null(await client.GetCoverUrlAsync(id, default));

        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task GetTracks_ReadsFromTheSourceThatListsThePlaylist_AndFallsBackToAnotherWhenItFails()
    {
        var stub = new Stub()
            .Metadata(Old, Metadata((IdA, "A", 1)))
            .Metadata(New, Metadata((IdA, "A", 1)))
            .Tracks(Old, IdA, TracksFile("From old"));

        var result = await CreateClient(stub, Old, New).GetTracksAsync(IdA, default);

        Assert.Equal("From old", Assert.Single(result!.Tracks).Title);
        Assert.Equal(New, result.Playlist.SourceBaseUrl);
        Assert.Contains($"{New}/playlists/pretty/{IdA}.json", stub.Requests);
    }

    [Fact]
    public async Task GetCover_ReadsTheOEmbedThumbnail_AndIsNullWhenItFails()
    {
        var stub = new Stub();
        stub.Routes[$"https://open.spotify.com/oembed?url=https://open.spotify.com/playlist/{IdA}"] =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "thumbnail_url": "https://image-cdn.spotifycdn.com/x" }""") };
        var client = CreateClient(stub, Old);

        Assert.Equal("https://image-cdn.spotifycdn.com/x", await client.GetCoverUrlAsync(IdA, default));
        Assert.Null(await client.GetCoverUrlAsync(IdB, default));
    }

    [Fact]
    public void ANewSourceAppendsToTheDefaultsWhenConfiguredByIndex()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MusicEnricher:SpotifyPlaylistArchiveSources:2"] = "https://example.test/archive-3",
            })
            .Build();
        var options = new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" };

        config.GetSection("MusicEnricher").Bind(options);

        Assert.Equal(3, options.SpotifyPlaylistArchiveSources.Length);
        Assert.Equal("https://example.test/archive-3", options.SpotifyPlaylistArchiveSources[^1]);
        Assert.Contains("spotify-playlist-archive-2", options.SpotifyPlaylistArchiveSources[1]);
    }
}
