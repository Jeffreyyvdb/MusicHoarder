using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Endpoints;

namespace MusicHoarder.Api.Tests.Catalog;

public class CatalogAudioProxyTests
{
    private static Task<IResult> Stream(HttpContext context, ICatalogAudioResolver resolver, FakeHandler handler) =>
        CatalogEndpoints.StreamFull("42", context, resolver, FakeHandler.Client(handler),
            NullLogger<CatalogAudioResolver>.Instance, default);

    private static HttpResponseMessage Partial(byte[] body, string contentRange)
    {
        var response = CatalogTestData.Response(HttpStatusCode.PartialContent, body);
        response.Content.Headers.ContentRange = ContentRangeHeaderValue.Parse(contentRange);
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    [Fact]
    public async Task ForwardsTheClientsRange_AndPassesA206Through_WithItsHeaders()
    {
        var resolver = new FakeResolver(_ => CatalogTestData.Audio());
        var handler = new FakeHandler(_ => Partial([9, 8, 7], "bytes 100-102/5000"));
        var context = CatalogTestData.ContextWithBody("bytes=100-102");

        await Stream(context, resolver, handler);

        Assert.Equal("bytes=100-102", handler.Requests.Single().Headers.GetValues("Range").Single());
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("audio/mp4", context.Response.ContentType);
        Assert.Equal(3, context.Response.ContentLength);
        Assert.Equal("bytes 100-102/5000", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(new byte[] { 9, 8, 7 }, CatalogTestData.BodyOf(context));
    }

    [Fact]
    public async Task ARequestWithoutRange_SendsNone_AndStreamsTheWholeBodyAs200()
    {
        var resolver = new FakeResolver(_ => CatalogTestData.Audio());
        var handler = new FakeHandler(_ => CatalogTestData.Response(HttpStatusCode.OK, [1, 2, 3, 4, 5]));
        var context = CatalogTestData.ContextWithBody();

        await Stream(context, resolver, handler);

        Assert.False(handler.Requests.Single().Headers.Contains("Range"));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, CatalogTestData.BodyOf(context));
    }

    [Fact]
    public async Task AnUnsatisfiableRange_PassesThroughAs416()
    {
        var resolver = new FakeResolver(_ => CatalogTestData.Audio());
        var handler = new FakeHandler(_ => CatalogTestData.Response(HttpStatusCode.RequestedRangeNotSatisfiable, []));
        var context = CatalogTestData.ContextWithBody("bytes=99999-");

        await Stream(context, resolver, handler);

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task AnExpiredUrl_IsDropped_AndResolvedOnceMore(HttpStatusCode stale)
    {
        var resolver = new FakeResolver(n => CatalogTestData.Audio(host: $"rr{n + 1}.googlevideo.com"));
        var handler = new FakeHandler(request => request.RequestUri!.Host.StartsWith("rr1", StringComparison.Ordinal)
            ? CatalogTestData.Response(stale)
            : CatalogTestData.Response(HttpStatusCode.OK, [5, 5]));
        var context = CatalogTestData.ContextWithBody();

        await Stream(context, resolver, handler);

        Assert.Equal(1, resolver.Invalidations);
        Assert.Equal(2, resolver.Resolves);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(new byte[] { 5, 5 }, CatalogTestData.BodyOf(context));
    }

    [Fact]
    public async Task AUrlThatIsStillRefused_AfterTheRetry_Answers502_AndTriesNoThird()
    {
        var resolver = new FakeResolver(_ => CatalogTestData.Audio());
        var handler = new FakeHandler(_ => CatalogTestData.Response(HttpStatusCode.Forbidden));

        var result = await Stream(CatalogTestData.ContextWithBody(), resolver, handler);

        Assert.Equal(StatusCodes.Status502BadGateway, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AnUpstreamServerError_Answers502()
    {
        var resolver = new FakeResolver(_ => CatalogTestData.Audio());
        var handler = new FakeHandler(_ => CatalogTestData.Response(HttpStatusCode.InternalServerError));

        var result = await Stream(CatalogTestData.ContextWithBody(), resolver, handler);

        Assert.Equal(StatusCodes.Status502BadGateway, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, resolver.Invalidations);
    }

    [Theory]
    [InlineData("rr1.example.com")]
    [InlineData("googlevideo.com.evil.example")]
    [InlineData("169.254.169.254")]
    public async Task AResolvedUrlOutsideGooglevideo_IsNeverFetched(string host)
    {
        var resolver = new FakeResolver(_ => CatalogTestData.Audio(host: host));
        var handler = new FakeHandler(_ => CatalogTestData.Response(HttpStatusCode.OK, [1]));

        var result = await Stream(CatalogTestData.ContextWithBody(), resolver, handler);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ANonNumericId_Answers400_BeforeResolving()
    {
        var resolver = new FakeResolver(_ => CatalogTestData.Audio());
        var handler = new FakeHandler(_ => CatalogTestData.Response(HttpStatusCode.OK, [1]));

        var result = await CatalogEndpoints.StreamFull("../x", CatalogTestData.ContextWithBody(), resolver,
            FakeHandler.Client(handler), NullLogger<CatalogAudioResolver>.Instance, default);

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, resolver.Resolves);
    }

    [Fact]
    public async Task AResolveFailure_IsMappedLikeTheResolveRoute()
    {
        var resolver = new ThrowingResolver(new CatalogResolveException(CatalogResolveFailure.NotFound, "nothing"));
        var handler = new FakeHandler(_ => CatalogTestData.Response(HttpStatusCode.OK, [1]));

        var result = await Stream(CatalogTestData.ContextWithBody(), resolver, handler);

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private sealed class ThrowingResolver(CatalogResolveException failure) : ICatalogAudioResolver
    {
        public Task<ResolvedAudio> ResolveAsync(string deezerId, CancellationToken ct) => throw failure;

        public void Invalidate(string deezerId)
        {
        }
    }
}
