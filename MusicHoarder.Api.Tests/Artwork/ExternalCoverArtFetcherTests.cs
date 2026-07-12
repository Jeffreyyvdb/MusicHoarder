using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.CoverArtArchive;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Artwork;

public class ExternalCoverArtFetcherTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly ExternalCoverArtQuery FullQuery = new("mbid-1", "rg-1", "Artist", "Album");

    [Fact]
    public async Task CoverArtArchiveHitShortCircuitsTheChain()
    {
        var caa = new StubCaa { Release = new CoverArtArchiveImage(Png, "image/png") };
        var deezer = new StubDeezer();
        var apple = new StubApple();
        var fetcher = CreateFetcher(caa, deezer, apple);

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.NotNull(result.Cover);
        Assert.Equal("coverartarchive", result.Cover.Source);
        Assert.Equal("image/png", result.Cover.ContentType);
        Assert.False(result.HadTransientFailure);
        Assert.Equal(0, deezer.SearchCalls);
        Assert.Equal(0, apple.SearchCalls);
    }

    [Fact]
    public async Task FallsBackToReleaseGroupWhenReleaseHasNoArt()
    {
        var caa = new StubCaa { ReleaseGroup = new CoverArtArchiveImage(Png, "image/png") };
        var fetcher = CreateFetcher(caa, new StubDeezer(), new StubApple());

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.Equal("coverartarchive", result.Cover?.Source);
        Assert.Equal(1, caa.ReleaseCalls);
        Assert.Equal(1, caa.ReleaseGroupCalls);
    }

    [Fact]
    public async Task MissingMbidsSkipCoverArtArchive()
    {
        var caa = new StubCaa { Release = new CoverArtArchiveImage(Png, "image/png") };
        var deezer = new StubDeezer { AlbumId = "d1", CoverUrl = "https://cdn.deezer.example/cover_xl.jpg" };
        var http = new UrlKeyedImageHandler { ["https://cdn.deezer.example/cover_xl.jpg"] = (HttpStatusCode.OK, Png, "image/jpeg") };
        var fetcher = CreateFetcher(caa, deezer, new StubApple(), http);

        var result = await fetcher.FetchAsync(new ExternalCoverArtQuery(null, null, "Artist", "Album"));

        Assert.Equal(0, caa.ReleaseCalls + caa.ReleaseGroupCalls);
        Assert.Equal("deezer", result.Cover?.Source);
    }

    [Fact]
    public async Task MissingAlbumSkipsDeezerAndItunes()
    {
        var deezer = new StubDeezer();
        var apple = new StubApple();
        var fetcher = CreateFetcher(new StubCaa(), deezer, apple);

        var result = await fetcher.FetchAsync(new ExternalCoverArtQuery(null, null, "Artist", null));

        Assert.Null(result.Cover);
        Assert.Equal(0, deezer.SearchCalls);
        Assert.Equal(0, apple.SearchCalls);
    }

    [Fact]
    public async Task DisabledProvidersAreSkipped()
    {
        var caa = new StubCaa { Release = new CoverArtArchiveImage(Png, "image/png") };
        var deezer = new StubDeezer { AlbumId = "d1", CoverUrl = "https://cdn.deezer.example/c.jpg" };
        var fetcher = CreateFetcher(caa, deezer, new StubApple(),
            configure: o =>
            {
                o.EnableCoverArtArchiveCovers = false;
                o.EnableDeezerCovers = false;
                o.EnableAppleMusicCovers = false;
            });

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.Null(result.Cover);
        Assert.Equal(0, caa.ReleaseCalls);
        Assert.Equal(0, deezer.SearchCalls);
    }

    [Fact]
    public async Task RateLimitedProviderFallsThroughAndFlagsTransientFailure()
    {
        var caa = new StubCaa { Throws = new ProviderRateLimitedException(TimeSpan.FromSeconds(5)) };
        var deezer = new StubDeezer { AlbumId = "d1", CoverUrl = "https://cdn.deezer.example/c.jpg" };
        var http = new UrlKeyedImageHandler { ["https://cdn.deezer.example/c.jpg"] = (HttpStatusCode.OK, Png, "image/jpeg") };
        var fetcher = CreateFetcher(caa, deezer, new StubApple(), http);

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.Equal("deezer", result.Cover?.Source);
        Assert.True(result.HadTransientFailure);
    }

    [Fact]
    public async Task TooSmallImageIsRejectedAndChainContinues()
    {
        var deezer = new StubDeezer { AlbumId = "d1", CoverUrl = "https://cdn.deezer.example/tiny.jpg" };
        var apple = new StubApple { AlbumId = "a1", ArtworkUrl = "https://is1.example/100x100bb.jpg" };
        var http = new UrlKeyedImageHandler
        {
            ["https://cdn.deezer.example/tiny.jpg"] = (HttpStatusCode.OK, [0xFF, 0xD8, 0xFF], "image/jpeg"),
            ["https://is1.example/3000x3000bb.jpg"] = (HttpStatusCode.OK, Png, "image/png"),
        };
        var fetcher = CreateFetcher(new StubCaa(), deezer, apple, http, o => o.ExternalCoverArtMinImageBytes = 8);

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.Equal("itunes", result.Cover?.Source);
    }

    [Fact]
    public async Task ItunesArtworkIsUpgradedTo3000()
    {
        var apple = new StubApple { AlbumId = "a1", ArtworkUrl = "https://is1.example/path/100x100bb.jpg" };
        var http = new UrlKeyedImageHandler
        {
            ["https://is1.example/path/3000x3000bb.jpg"] = (HttpStatusCode.OK, Png, "image/jpeg"),
        };
        var fetcher = CreateFetcher(new StubCaa(), new StubDeezer(), apple, http);

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.Equal("itunes", result.Cover?.Source);
        Assert.Equal(["https://is1.example/path/3000x3000bb.jpg"], http.RequestedUrls);
    }

    [Fact]
    public async Task ItunesFallsBackToOriginalUrlWhenUpgradeMissing()
    {
        var apple = new StubApple { AlbumId = "a1", ArtworkUrl = "https://is1.example/path/100x100bb.jpg" };
        var http = new UrlKeyedImageHandler
        {
            ["https://is1.example/path/100x100bb.jpg"] = (HttpStatusCode.OK, Png, "image/jpeg"),
        };
        var fetcher = CreateFetcher(new StubCaa(), new StubDeezer(), apple, http);

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.Equal("itunes", result.Cover?.Source);
        Assert.Equal(2, http.RequestedUrls.Count);
        Assert.Equal("https://is1.example/path/100x100bb.jpg", http.RequestedUrls[1]);
    }

    [Fact]
    public async Task SniffedMimeOverridesBogusContentType()
    {
        var deezer = new StubDeezer { AlbumId = "d1", CoverUrl = "https://cdn.deezer.example/c" };
        var http = new UrlKeyedImageHandler
        {
            ["https://cdn.deezer.example/c"] = (HttpStatusCode.OK, Png, "text/html"),
        };
        var fetcher = CreateFetcher(new StubCaa(), deezer, new StubApple(), http);

        var result = await fetcher.FetchAsync(FullQuery);

        Assert.Equal("image/png", result.Cover?.ContentType);
    }

    [Theory]
    [InlineData("https://is1.example/x/100x100bb.jpg", "https://is1.example/x/3000x3000bb.jpg")]
    [InlineData("https://is1.example/x/100x100bb.png", "https://is1.example/x/3000x3000bb.png")]
    [InlineData("https://is1.example/x/600x600bb.jpg", "https://is1.example/x/600x600bb.jpg")]
    public void UpgradeItunesArtworkUrlRewritesOnlyTheThumbnailSegment(string input, string expected)
        => Assert.Equal(expected, ExternalCoverArtFetcher.UpgradeItunesArtworkUrl(input));

    private static ExternalCoverArtFetcher CreateFetcher(
        StubCaa caa,
        StubDeezer deezer,
        StubApple apple,
        UrlKeyedImageHandler? http = null,
        Action<MusicEnricherOptions>? configure = null)
    {
        var options = new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            ExternalCoverArtMinImageBytes = 4,
        };
        configure?.Invoke(options);

        return new ExternalCoverArtFetcher(
            caa, deezer, apple,
            new HttpClient(http ?? new UrlKeyedImageHandler()),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<ExternalCoverArtFetcher>.Instance);
    }

    private sealed class StubCaa : ICoverArtArchiveClient
    {
        public CoverArtArchiveImage? Release { get; set; }
        public CoverArtArchiveImage? ReleaseGroup { get; set; }
        public Exception? Throws { get; set; }
        public int ReleaseCalls { get; private set; }
        public int ReleaseGroupCalls { get; private set; }

        public Task<CoverArtArchiveImage?> GetReleaseFrontAsync(string releaseMbid, CancellationToken ct = default)
        {
            ReleaseCalls++;
            return Throws is not null ? throw Throws : Task.FromResult(Release);
        }

        public Task<CoverArtArchiveImage?> GetReleaseGroupFrontAsync(string releaseGroupMbid, CancellationToken ct = default)
        {
            ReleaseGroupCalls++;
            return Throws is not null ? throw Throws : Task.FromResult(ReleaseGroup);
        }
    }

    private sealed class StubDeezer : IDeezerCatalogService
    {
        public string? AlbumId { get; set; }
        public string? CoverUrl { get; set; }
        public int SearchCalls { get; private set; }

        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(AlbumId);
        }

        public Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default)
            => Task.FromResult(AlbumId is null ? null : new DeezerAlbumDetail(AlbumId, "Album", "Artist", 2026, CoverUrl, []));

        public Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
            => Task.FromResult<DeezerCatalogTrack?>(null);

        public Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerCatalogTrack>>([]);

        public Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default)
            => Task.FromResult<DeezerCatalogTrack?>(null);

        public Task<IReadOnlyList<DeezerGenre>> GetGenresAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerGenre>>([]);

        public Task<IReadOnlyList<DeezerPlaylistSummary>> GetChartPlaylistsAsync(long? genreId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerPlaylistSummary>>([]);

        public Task<IReadOnlyList<DeezerPlaylistSummary>> SearchPlaylistsAsync(string query, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerPlaylistSummary>>([]);

        public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default)
            => Task.FromResult<DeezerPlaylistSummary?>(null);

        public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default)
            => Task.FromResult(new DeezerPlaylistTracksResult([], IsComplete: true));
    }

    private sealed class StubApple : IAppleMusicCatalogService
    {
        public string? AlbumId { get; set; }
        public string? ArtworkUrl { get; set; }
        public int SearchCalls { get; private set; }

        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(AlbumId);
        }

        public Task<AppleAlbumDetail?> GetAlbumAsync(string collectionId, CancellationToken ct = default)
            => Task.FromResult(AlbumId is null ? null : new AppleAlbumDetail(AlbumId, "Album", "Artist", 2026, ArtworkUrl, []));

        public Task<IReadOnlyList<AppleMusicCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AppleMusicCatalogTrack>>([]);
    }

    private sealed class UrlKeyedImageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body, string? ContentType)> _responses = [];

        public List<string> RequestedUrls { get; } = [];

        public (HttpStatusCode, byte[], string?) this[string url]
        {
            set => _responses[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            if (!_responses.TryGetValue(url, out var entry))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new ByteArrayContent(entry.Body);
            if (entry.ContentType is not null)
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(entry.ContentType);
            }

            return Task.FromResult(new HttpResponseMessage(entry.Status) { Content = content });
        }
    }
}
