using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.CoverArtArchive;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Artwork;

public class CoverArtArchiveClientTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task ReturnsFront1200WhenAvailable()
    {
        var handler = new UrlKeyedHandler
        {
            ["https://coverartarchive.org/release/mbid-1/front-1200"] = (HttpStatusCode.OK, Png, "image/png"),
        };
        var client = CreateClient(handler);

        var image = await client.GetReleaseFrontAsync("mbid-1");

        Assert.NotNull(image);
        Assert.Equal(Png, image.Bytes);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(["https://coverartarchive.org/release/mbid-1/front-1200"], handler.RequestedUrls);
    }

    [Fact]
    public async Task FallsBackToOriginalFrontWhenFront1200Missing()
    {
        var handler = new UrlKeyedHandler
        {
            ["https://coverartarchive.org/release/mbid-1/front-1200"] = (HttpStatusCode.NotFound, [], null),
            ["https://coverartarchive.org/release/mbid-1/front"] = (HttpStatusCode.OK, Png, "image/png"),
        };
        var client = CreateClient(handler);

        var image = await client.GetReleaseFrontAsync("mbid-1");

        Assert.NotNull(image);
        Assert.Equal(2, handler.RequestedUrls.Count);
        Assert.Equal("https://coverartarchive.org/release/mbid-1/front", handler.RequestedUrls[1]);
    }

    [Fact]
    public async Task ReturnsNullWhenNoArtRegistered()
    {
        var handler = new UrlKeyedHandler(); // every URL 404s
        var client = CreateClient(handler);

        Assert.Null(await client.GetReleaseFrontAsync("mbid-1"));
        Assert.Null(await client.GetReleaseGroupFrontAsync("rg-1"));
        Assert.Contains("https://coverartarchive.org/release-group/rg-1/front-1200", handler.RequestedUrls);
    }

    [Fact]
    public async Task ThrowsRateLimitedOn503()
    {
        var handler = new UrlKeyedHandler
        {
            ["https://coverartarchive.org/release/mbid-1/front-1200"] = (HttpStatusCode.ServiceUnavailable, [], null),
        };
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ProviderRateLimitedException>(() => client.GetReleaseFrontAsync("mbid-1"));
    }

    [Fact]
    public async Task SkipsBlankMbidWithoutRequest()
    {
        var handler = new UrlKeyedHandler();
        var client = CreateClient(handler);

        Assert.Null(await client.GetReleaseFrontAsync(" "));
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task SendsMusicBrainzUserAgent()
    {
        var handler = new UrlKeyedHandler
        {
            ["https://coverartarchive.org/release/mbid-1/front-1200"] = (HttpStatusCode.OK, Png, "image/png"),
        };
        var client = CreateClient(handler, userAgent: "TestAgent/1.0 (test@example.com)");

        await client.GetReleaseFrontAsync("mbid-1");

        Assert.Equal("TestAgent/1.0 (test@example.com)", handler.LastUserAgent);
    }

    private static CoverArtArchiveClient CreateClient(HttpMessageHandler handler, string? userAgent = null)
    {
        var options = new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            // Keep the shared static limiter from slowing the suite down.
            CoverArtArchiveRequestsPerSecond = 5,
        };
        if (userAgent is not null)
        {
            options.MusicBrainzUserAgent = userAgent;
        }

        return new CoverArtArchiveClient(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<CoverArtArchiveClient>.Instance);
    }

    private sealed class UrlKeyedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body, string? ContentType)> _responses = [];

        public List<string> RequestedUrls { get; } = [];
        public string? LastUserAgent { get; private set; }

        public (HttpStatusCode, byte[], string?) this[string url]
        {
            set => _responses[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            LastUserAgent = request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : null;

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
