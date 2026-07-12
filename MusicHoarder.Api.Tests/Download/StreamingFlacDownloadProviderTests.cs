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
/// resolution (track id → URL; ISRC → id via the catalog client; neither ⇒ Missing without a call).
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

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private StreamingFlacDownloadProvider CreateProvider(
        FakeSidecarHandler handler,
        string sidecarUrl = "http://spotiflac:8000",
        ISpotifyCatalogSearchService? catalog = null,
        string spotifyClientId = "",
        string spotifyClientSecret = "")
    {
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new TestOptionsMonitor<StreamingFlacOptions>(new StreamingFlacOptions { SidecarUrl = sidecarUrl });
        var client = new StreamingFlacSidecarClient(httpClient, options, NullLogger<StreamingFlacSidecarClient>.Instance);
        var spotifyOptions = Microsoft.Extensions.Options.Options.Create(
            new SpotifyOptions { ClientId = spotifyClientId, ClientSecret = spotifyClientSecret });
        return new StreamingFlacDownloadProvider(
            client,
            catalog ?? new FakeCatalog(null),
            spotifyOptions,
            options,
            NullLogger<StreamingFlacDownloadProvider>.Instance);
    }

    private DownloadRequest Request(string? trackId, string? isrc = "USABC1234567") =>
        new("Artist", "Title", "Album", isrc, 200_000, _stagingDir, trackId);

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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/acquire"))
            {
                AcquireCalls++;
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

    private sealed class FakeCatalog(string? resolvedId) : ISpotifyCatalogSearchService
    {
        public string? LastIsrc { get; private set; }

        public Task<string?> SearchTrackIdByIsrcAsync(string clientId, string clientSecret, string isrc, CancellationToken ct = default)
        {
            LastIsrc = isrc;
            return Task.FromResult(resolvedId);
        }

        public Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracksAsync(string clientId, string clientSecret, string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([]);
        public Task<SpotifyCatalogTrack?> GetTrackAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default) => Task.FromResult<SpotifyCatalogTrack?>(null);
        public Task<string?> GetTrackAlbumIdAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> SearchAlbumIdAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<SpotifyAlbumDetail?> GetAlbumAsync(string clientId, string clientSecret, string albumId, CancellationToken ct = default) => Task.FromResult<SpotifyAlbumDetail?>(null);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
