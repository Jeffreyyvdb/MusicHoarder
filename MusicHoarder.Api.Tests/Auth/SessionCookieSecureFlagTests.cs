using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Auth;

public sealed class SessionCookieSecureFlagTests
{
    private static ISessionCookieService Create(string publicBaseUrl)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.Configure<AuthOptions>(_ => { });
        services.Configure<FrontendOptions>(o => o.PublicBaseUrl = publicBaseUrl);
        services.AddSingleton<ISessionCookieService, SessionCookieService>();
        return services.BuildServiceProvider().GetRequiredService<ISessionCookieService>();
    }

    private static DefaultHttpContext PlainHttpContext() => new() { Request = { Scheme = "http" } };

    [Fact]
    public void Session_cookies_are_secure_when_public_origin_is_https_even_on_plain_http_hop()
    {
        var service = Create("https://music.example.com");
        var ctx = PlainHttpContext();

        service.Write(ctx, Guid.NewGuid());
        service.WriteAlts(ctx, [Guid.NewGuid()]);

        var headers = ctx.Response.Headers.SetCookie.ToArray();
        Assert.Equal(2, headers.Length);
        Assert.All(headers, h => Assert.Contains("secure", h!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cleared_cookies_are_secure_when_public_origin_is_https()
    {
        var service = Create("https://music.example.com");
        var ctx = PlainHttpContext();

        service.Clear(ctx);

        Assert.Contains("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_cookie_is_not_secure_when_public_origin_is_http_and_request_is_plain()
    {
        var service = Create("http://localhost:3000");
        var ctx = PlainHttpContext();

        service.Write(ctx, Guid.NewGuid());

        Assert.DoesNotContain("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
