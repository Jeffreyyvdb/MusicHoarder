using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.StreamingFlac;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// The "spotiflac" provider over a fake sidecar: unconfigured/not_found ⇒ Missing (chain falls
/// through), transport/error ⇒ Failed (chain stops), and a real file ⇒ Ok. Also covers Spotify-URL
/// resolution (track id → URL; ISRC → id via the catalog client; neither ⇒ Missing without a call) and
/// the explicit preference (a clean edit is swapped for its explicit edition when Spotify has one).
/// </summary>
public class StreamingFlacDownloadProviderTests : IDisposable
{
    private readonly string _stagingDir = Path.Combine(Path.GetTempPath(), "mh-spotiflac-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stagingDir))
            Directory.Delete(_stagingDir, recursive: true);
    }

    [Fact]
    public async Task Unconfigured_ReturnsMissing_WithoutCallingSidecar()
    {
        var handler = new FakeSidecarHandler();
        var provider = CreateProvider(handler, sidecarUrl: "");

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.False(result.Success);
        Assert.True(result.NotFound);
        Assert.Equal(0, handler.AcquireCalls);
    }

    [Fact]
    public async Task Ok_WithRealFile_ReturnsOkPointingAtIt()
    {
        Directory.CreateDirectory(_stagingDir);
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body =>
            {
                // Simulate the sidecar writing the FLAC into the shared staging dir.
                var stem = ReadStem(body);
                var path = Path.Combine(_stagingDir, stem + ".flac");
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                return Ok(path, "qobuz");
            }
        };
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.True(result.Success);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath!));
        Assert.EndsWith(".flac", result.FilePath);
    }

    [Fact]
    public async Task NotFound_ReturnsMissing_SoChainFallsThrough()
    {
        var handler = new FakeSidecarHandler { AcquireResponder = _ => NotFound("no lossless source") };
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.False(result.Success);
        Assert.True(result.NotFound); // Missing
    }

    [Fact]
    public async Task Error_ReturnsFailed_SoChainStops()
    {
        var handler = new FakeSidecarHandler { AcquireResponder = _ => Error("community server 502") };
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.False(result.Success);
        Assert.False(result.NotFound); // Failed
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    public async Task Unreachable_ReturnsUnavailable_SoChainFallsThrough(HttpRequestError error)
    {
        // The container is down or mid-redeploy: "Name or service not known (spotiflac:8000)". Not a
        // failed attempt against the track — the chain must move on and remember to come back.
        var handler = new FakeSidecarHandler
        {
            AcquireThrows = new HttpRequestException(error, "Name or service not known (spotiflac:8000)"),
        };
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.False(result.Success);
        Assert.True(result.Unavailable);
        Assert.False(result.NotFound);
        Assert.True(result.FallsThrough);
        Assert.Contains("Name or service not known", result.Error);
    }

    [Fact]
    public async Task OtherTransportException_StaysFailed_SoChainStops()
    {
        // A reachable-but-broken sidecar (e.g. a mid-body IO error) is still a real transient failure.
        var handler = new FakeSidecarHandler { AcquireThrows = new IOException("connection reset mid-body") };
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.False(result.Success);
        Assert.False(result.Unavailable);
        Assert.False(result.NotFound);
    }

    [Fact]
    public async Task Http500_ReturnsFailed()
    {
        var handler = new FakeSidecarHandler { AcquireStatusCode = HttpStatusCode.InternalServerError };
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.False(result.Success);
        Assert.False(result.NotFound); // Failed — transport-level
    }

    [Fact]
    public async Task OkButFileMissing_ReturnsFailed()
    {
        // Sidecar claims success but nothing landed on our side of the volume (misconfig).
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = _ => Ok(Path.Combine(_stagingDir, "ghost.flac"), "tidal")
        };
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: "abc"), default);

        Assert.False(result.Success);
        Assert.False(result.NotFound); // Failed, not silently Downloaded
    }

    [Fact]
    public async Task TrackId_IsBuiltIntoSpotifyUrl()
    {
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body => { sentUrl = ReadSpotifyUrl(body); return NotFound("stop here"); }
        };
        var provider = CreateProvider(handler);

        await provider.DownloadAsync(Request(trackId: "4cOdK2wGLETKBW3PvgPWqT"), default);

        Assert.Equal("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", sentUrl);
    }

    [Fact]
    public async Task NoTrackIdNoIsrc_ReturnsMissing_WithoutCallingSidecar()
    {
        var handler = new FakeSidecarHandler();
        var provider = CreateProvider(handler);

        var result = await provider.DownloadAsync(Request(trackId: null, isrc: null), default);

        Assert.True(result.NotFound);
        Assert.Equal(0, handler.AcquireCalls);
    }

    [Fact]
    public async Task NoTrackId_ResolvesIsrcToIdViaCatalog()
    {
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body => { sentUrl = ReadSpotifyUrl(body); return NotFound("stop"); }
        };
        var catalog = new FakeCatalog("resolvedId123");
        var provider = CreateProvider(handler, catalog: catalog, spotifyClientId: "id", spotifyClientSecret: "secret");

        await provider.DownloadAsync(Request(trackId: null, isrc: "USABC1234567"), default);

        Assert.Equal("USABC1234567", catalog.LastIsrc);
        Assert.Equal("https://open.spotify.com/track/resolvedId123", sentUrl);
    }

    [Fact]
    public async Task CleanEdit_IsSwappedForItsExplicitEdition()
    {
        Directory.CreateDirectory(_stagingDir);
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body =>
            {
                sentUrl = ReadSpotifyUrl(body);
                var path = Path.Combine(_stagingDir, ReadStem(body) + ".flac");
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                return Ok(path, "qobuz");
            }
        };
        var catalog = new FakeCatalog(null)
        {
            Tracks = { ["clean1"] = Track("clean1", isExplicit: false, isrc: "USCLN0000001") },
            SearchResults = [Track("clean1", isExplicit: false, isrc: "USCLN0000001"), Track("explicit1", isExplicit: true, isrc: "USEXP0000001")],
        };
        var provider = CreateProvider(handler, catalog: catalog, spotifyClientId: "id", spotifyClientSecret: "secret");

        var result = await provider.DownloadAsync(Request(trackId: "clean1"), default);

        Assert.True(result.Success);
        Assert.Equal("https://open.spotify.com/track/explicit1", sentUrl);
        // The file is stamped with the ISRC of the edition actually fetched, not the clean one.
        Assert.Equal("USEXP0000001", result.Isrc);
        Assert.Equal("track:Title artist:Artist", catalog.LastQuery);
    }

    [Fact]
    public async Task ExplicitTrack_IsKept_WithoutSearching()
    {
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body => { sentUrl = ReadSpotifyUrl(body); return NotFound("stop"); }
        };
        var catalog = new FakeCatalog(null) { Tracks = { ["explicit1"] = Track("explicit1", isExplicit: true) } };
        var provider = CreateProvider(handler, catalog: catalog, spotifyClientId: "id", spotifyClientSecret: "secret");

        await provider.DownloadAsync(Request(trackId: "explicit1"), default);

        Assert.Equal("https://open.spotify.com/track/explicit1", sentUrl);
        Assert.Null(catalog.LastQuery);
    }

    [Fact]
    public async Task CleanTrack_WithoutAnExplicitEdition_IsKept()
    {
        Directory.CreateDirectory(_stagingDir);
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body =>
            {
                sentUrl = ReadSpotifyUrl(body);
                var path = Path.Combine(_stagingDir, ReadStem(body) + ".flac");
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                return Ok(path, "qobuz");
            }
        };
        var catalog = new FakeCatalog(null)
        {
            Tracks = { ["clean1"] = Track("clean1", isExplicit: false) },
            SearchResults = [Track("clean1", isExplicit: false)],
        };
        var provider = CreateProvider(handler, catalog: catalog, spotifyClientId: "id", spotifyClientSecret: "secret");

        var result = await provider.DownloadAsync(Request(trackId: "clean1"), default);

        Assert.Equal("https://open.spotify.com/track/clean1", sentUrl);
        Assert.Null(result.Isrc); // the caller keeps the requested ISRC
    }

    [Fact]
    public async Task PreferExplicitOff_KeepsTheRequestedEdition_WithoutLookups()
    {
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body => { sentUrl = ReadSpotifyUrl(body); return NotFound("stop"); }
        };
        var catalog = new FakeCatalog(null)
        {
            Tracks = { ["clean1"] = Track("clean1", isExplicit: false) },
            SearchResults = [Track("explicit1", isExplicit: true)],
        };
        var provider = CreateProvider(handler, catalog: catalog, spotifyClientId: "id", spotifyClientSecret: "secret",
            preferExplicit: false);

        await provider.DownloadAsync(Request(trackId: "clean1"), default);

        Assert.Equal("https://open.spotify.com/track/clean1", sentUrl);
        Assert.Equal(0, catalog.GetTrackCalls);
    }

    [Fact]
    public async Task ExplicitLookupFailure_KeepsTheRequestedEdition()
    {
        // A Spotify hiccup must never cost the download itself: fall back to the id as requested.
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body => { sentUrl = ReadSpotifyUrl(body); return NotFound("stop"); }
        };
        var catalog = new FakeCatalog(null) { GetTrackThrows = new HttpRequestException("spotify 503") };
        var provider = CreateProvider(handler, catalog: catalog, spotifyClientId: "id", spotifyClientSecret: "secret");

        await provider.DownloadAsync(Request(trackId: "clean1"), default);

        Assert.Equal("https://open.spotify.com/track/clean1", sentUrl);
    }

    [Fact]
    public async Task NoSpotifyCredentials_KeepsTheRequestedEdition_WithoutLookups()
    {
        string? sentUrl = null;
        var handler = new FakeSidecarHandler
        {
            AcquireResponder = body => { sentUrl = ReadSpotifyUrl(body); return NotFound("stop"); }
        };
        var catalog = new FakeCatalog(null) { Tracks = { ["clean1"] = Track("clean1", isExplicit: false) } };
        var provider = CreateProvider(handler, catalog: catalog);

        await provider.DownloadAsync(Request(trackId: "clean1"), default);

        Assert.Equal("https://open.spotify.com/track/clean1", sentUrl);
        Assert.Equal(0, catalog.GetTrackCalls);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private StreamingFlacDownloadProvider CreateProvider(
        FakeSidecarHandler handler,
        string sidecarUrl = "http://spotiflac:8000",
        ISpotifyCatalogSearchService? catalog = null,
        string spotifyClientId = "",
        string spotifyClientSecret = "",
        bool preferExplicit = true)
    {
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new TestOptionsMonitor<StreamingFlacOptions>(
            new StreamingFlacOptions { SidecarUrl = sidecarUrl, PreferExplicit = preferExplicit });
        var client = new StreamingFlacSidecarClient(httpClient, options, NullLogger<StreamingFlacSidecarClient>.Instance);
        return new StreamingFlacDownloadProvider(
            client,
            catalog ?? new FakeCatalog(null),
            new FakeCredentials(spotifyClientId, spotifyClientSecret),
            options,
            NullLogger<StreamingFlacDownloadProvider>.Instance);
    }

    private DownloadRequest Request(string? trackId, string? isrc = "USABC1234567") =>
        new("Artist", "Title", "Album", isrc, 200_000, _stagingDir, trackId);

    // Matches Request(): "Artist" / "Title" at 200 s.
    private static SpotifyCatalogTrack Track(string id, bool isExplicit, string? isrc = null) =>
        new(id, "Title", "Artist", "Album", 2020, 1, 200_000, isrc, Explicit: isExplicit);

    private static string ReadStem(string body) => JsonDocument.Parse(body).RootElement.GetProperty("filename_stem").GetString()!;
    private static string ReadSpotifyUrl(string body) => JsonDocument.Parse(body).RootElement.GetProperty("spotify_url").GetString()!;

    private static string Ok(string file, string provider) =>
        JsonSerializer.Serialize(new { status = "ok", file, provider });
    private static string NotFound(string error) => JsonSerializer.Serialize(new { status = "not_found", error });
    private static string Error(string error) => JsonSerializer.Serialize(new { status = "error", error });

    private sealed class FakeSidecarHandler : HttpMessageHandler
    {
        public int AcquireCalls { get; private set; }
        public HttpStatusCode AcquireStatusCode { get; set; } = HttpStatusCode.OK;
        public Func<string, string>? AcquireResponder { get; set; }
        /// <summary>Thrown from the transport instead of answering — models an unreachable sidecar.</summary>
        public Exception? AcquireThrows { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/acquire"))
            {
                AcquireCalls++;
                if (AcquireThrows is not null)
                    throw AcquireThrows;
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                if (AcquireStatusCode != HttpStatusCode.OK)
                    return new HttpResponseMessage(AcquireStatusCode) { Content = new StringContent("boom") };
                var json = AcquireResponder?.Invoke(body) ?? NotFound("no responder");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            // /health or anything else
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\",\"providers\":[\"qobuz\"]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeCredentials(string clientId, string clientSecret) : ISpotifyAppCredentialsProvider
    {
        public Task<(string? ClientId, string? ClientSecret)> ResolveAsync(CancellationToken ct = default) =>
            Task.FromResult<(string?, string?)>(
                string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) ? (null, null) : (clientId, clientSecret));
    }

    private sealed class FakeCatalog(string? resolvedId) : ISpotifyCatalogSearchService
    {
        public string? LastIsrc { get; private set; }
        public string? LastQuery { get; private set; }
        public int GetTrackCalls { get; private set; }
        public Dictionary<string, SpotifyCatalogTrack> Tracks { get; } = [];
        public IReadOnlyList<SpotifyCatalogTrack> SearchResults { get; init; } = [];
        public Exception? GetTrackThrows { get; init; }

        public Task<string?> SearchTrackIdByIsrcAsync(string clientId, string clientSecret, string isrc, CancellationToken ct = default)
        {
            LastIsrc = isrc;
            return Task.FromResult(resolvedId);
        }

        public Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracksAsync(string clientId, string clientSecret, string query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(SearchResults);
        }

        public Task<SpotifyCatalogTrack?> GetTrackAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default)
        {
            GetTrackCalls++;
            if (GetTrackThrows is not null)
                throw GetTrackThrows;
            return Task.FromResult(Tracks.GetValueOrDefault(trackId));
        }

        public Task<string?> GetTrackAlbumIdAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> SearchAlbumIdAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SpotifyAlbumCandidate>> SearchAlbumCandidatesAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SpotifyAlbumCandidate>>([]);
        public Task<IReadOnlyList<SpotifyArtistCandidate>> SearchArtistCandidatesAsync(string clientId, string clientSecret, string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SpotifyArtistCandidate>>([]);
        public Task<SpotifyAlbumDetail?> GetAlbumAsync(string clientId, string clientSecret, string albumId, CancellationToken ct = default) => Task.FromResult<SpotifyAlbumDetail?>(null);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
