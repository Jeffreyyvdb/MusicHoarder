using Microsoft.AspNetCore.Http;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Endpoints;

public class FrontendUrlResolverTests
{
    [Fact]
    public void Resolve_with_blank_public_base_url_ignores_request_headers()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("other-origin.example");
        ctx.Request.Headers.Origin = "https://other-origin.example";
        ctx.Request.Headers.Referer = "https://other-origin.example/page";

        var result = FrontendUrlResolver.Resolve(ctx, new FrontendOptions { PublicBaseUrl = "" });

        Assert.DoesNotContain("other-origin.example", result);
        Assert.True(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void Resolve_uses_configured_public_base_url()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Origin = "https://other-origin.example";

        var result = FrontendUrlResolver.Resolve(ctx, new FrontendOptions { PublicBaseUrl = "https://music.example/" });

        Assert.Equal("https://music.example", result);
    }
}
