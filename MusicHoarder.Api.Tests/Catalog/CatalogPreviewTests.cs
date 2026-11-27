using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Tests.Deezer;

namespace MusicHoarder.Api.Tests.Catalog;

public class CatalogPreviewTests
{
    private const string ClipUrl = "https://cdnt-preview.dzcdn.net/stream/clip.mp3?hdnea=x";

    private static (CatalogPreviewService Service, FakeDeezerCatalogService Deezer, FakeHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, string? previewUrl = ClipUrl)
    {
        var deezer = new FakeDeezerCatalogService();
        if (previewUrl is not null)
            deezer.PreviewUrls["42"] = previewUrl;
        var handler = new FakeHandler(respond ?? (_ => CatalogTestData.Response(HttpStatusCode.OK, [1, 2, 3, 4], "audio/mpeg")));
        var service = new CatalogPreviewService(deezer, FakeHandler.Client(handler), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CatalogPreviewService>.Instance);
        return (service, deezer, handler);
    }

    [Fact]
    public async Task ReturnsTheClipBytes_ReadingAFreshUrlFromDeezer()
    {
        var (service, deezer, handler) = Build();

        var clip = await service.GetClipAsync("42", default);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, clip);
        Assert.Equal(1, deezer.PreviewUrlReads);
        Assert.Equal(ClipUrl, handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task ASecondRequest_IsServedFromTheCache()
    {
        var (service, deezer, handler) = Build();

        await service.GetClipAsync("42", default);
        var again = await service.GetClipAsync("42", default);

        Assert.Equal(4, again!.Length);
        Assert.Single(handler.Requests);
        Assert.Equal(1, deezer.PreviewUrlReads);
    }

    [Fact]
    public async Task ATrackWithoutAClip_ReturnsNull_AndFetchesNothing()
    {
        var (service, _, handler) = Build(previewUrl: null);

        Assert.Null(await service.GetClipAsync("42", default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/clip.mp3")]
    [InlineData("http://cdnt-preview.dzcdn.net/clip.mp3")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://dzcdn.net.evil.example/clip.mp3")]
    public async Task APreviewUrlOutsideDzcdn_IsNeverFetched(string url)
    {
        var (service, _, handler) = Build(previewUrl: url);

        Assert.Null(await service.GetClipAsync("42", default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AClipOverTheCap_IsRefused_ByDeclaredLength()
    {
        var (service, _, _) = Build(_ =>
        {
            var response = CatalogTestData.Response(HttpStatusCode.OK, [1], "audio/mpeg");
            response.Content = new StreamContent(new MemoryStream([1]));
            response.Content.Headers.ContentLength = CatalogPreviewService.MaxClipBytes + 1;
            return response;
        });

        Assert.Null(await service.GetClipAsync("42", default));
    }

    [Fact]
    public async Task AClipOverTheCap_IsRefused_WhenItOnlyShowsWhileReading()
    {
        var oversized = new byte[CatalogPreviewService.MaxClipBytes + 10];
        var (service, _, _) = Build(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(oversized)) };
            response.Headers.TransferEncodingChunked = true;
            return response;
        });

        Assert.Null(await service.GetClipAsync("42", default));
    }

    [Fact]
    public async Task ACdnFailure_Throws_SoTheEndpointCanAnswer502()
    {
        var (service, _, _) = Build(_ => CatalogTestData.Response(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetClipAsync("42", default));
    }

    [Fact]
    public async Task TheEndpoint_ServesRangeableMpegWithAPrivateCacheHeader()
    {
        var (service, _, _) = Build();
        var context = new DefaultHttpContext();

        var result = await CatalogEndpoints.Preview("42", context, service, NullLogger<CatalogPreviewService>.Instance, default);

        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal("audio/mpeg", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal("private, max-age=1800", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task TheEndpoint_Answers502_WhenTheCdnDropsTheClipMidway()
    {
        var (service, _, _) = Build(_ => throw new IOException("connection reset"));

        var result = await CatalogEndpoints.Preview("42", new DefaultHttpContext(), service, NullLogger<CatalogPreviewService>.Instance, default);

        Assert.Equal(StatusCodes.Status502BadGateway, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task TheEndpoint_Answers404NoPreview_WhenThereIsNoClip()
    {
        var (service, _, _) = Build(previewUrl: null);

        var result = await CatalogEndpoints.Preview("42", new DefaultHttpContext(), service, NullLogger<CatalogPreviewService>.Instance, default);

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12;DROP")]
    [InlineData("1.5")]
    public async Task TheEndpoint_Answers400_ForANonNumericId(string id)
    {
        var (service, deezer, _) = Build();

        var result = await CatalogEndpoints.Preview(id, new DefaultHttpContext(), service, NullLogger<CatalogPreviewService>.Instance, default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, deezer.PreviewUrlReads);
    }
}
