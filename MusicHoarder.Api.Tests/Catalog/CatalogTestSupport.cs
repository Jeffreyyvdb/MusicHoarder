using System.Net;
using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Catalog;

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static CatalogHttpClient Client(FakeHandler handler) => new(new HttpClient(handler));
}

internal sealed class FakeLocator(Func<DownloadRequest, ResolvedAudio> locate) : ICatalogAudioLocator
{
    private int calls;
    public int Calls => calls;
    public TaskCompletionSource? Gate { get; init; }

    public async Task<ResolvedAudio> LocateAsync(DownloadRequest track, CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        if (Gate is not null)
            await Gate.Task;
        return locate(track);
    }
}

internal sealed class FakeResolver(Func<int, ResolvedAudio> resolve) : ICatalogAudioResolver
{
    public int Resolves { get; private set; }
    public int Invalidations { get; private set; }

    public Task<ResolvedAudio> ResolveAsync(string deezerId, CancellationToken ct) => Task.FromResult(resolve(Resolves++));

    public void Invalidate(string deezerId, string staleUrl) => Invalidations++;
}

internal static class CatalogTestData
{
    public static ResolvedAudio Audio(string host = "rr1.googlevideo.com", long? expire = null) => new(
        $"https://{host}/videoplayback?id=1&expire={expire ?? DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeSeconds()}",
        "audio/mp4", "vid123", "Artist - Title (Official Video)", 223_000);

    public static HttpContext ContextWithBody(string? range = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (range is not null)
            context.Request.Headers.Range = range;
        return context;
    }

    public static byte[] BodyOf(HttpContext context)
    {
        var stream = (MemoryStream)context.Response.Body;
        return stream.ToArray();
    }

    public static HttpResponseMessage Response(HttpStatusCode status, byte[]? body = null, string contentType = "audio/mp4")
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new ByteArrayContent(body);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }
        return response;
    }
}
