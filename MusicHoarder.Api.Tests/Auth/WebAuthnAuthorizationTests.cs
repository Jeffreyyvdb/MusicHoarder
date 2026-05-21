using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Composition;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// Guards the access rules the WebAuthn enrollment/management endpoints rely on
/// (<see cref="RouteHandlerBuilderExtensions.RequireOwner"/>) plus the relying-party derivation
/// that feeds the FIDO2 configuration.
/// </summary>
public class WebAuthnAuthorizationTests
{
    [Fact]
    public async Task RequireOwner_rejects_anonymous_with_401()
    {
        var result = await Invoke(currentUser: null);
        var status = Assert.IsType<int>(GetStatusCode(result));
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task RequireOwner_rejects_demo_with_403()
    {
        var result = await Invoke(TestCurrentUserAccessor.DemoUser);
        var status = Assert.IsType<int>(GetStatusCode(result));
        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task RequireOwner_allows_owner_through()
    {
        var result = await Invoke(TestCurrentUserAccessor.OwnerUser);
        Assert.Equal("next", result);
    }

    [Fact]
    public void RelyingParty_derives_rp_id_and_origin_from_frontend_url()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions(),
            new FrontendOptions { PublicBaseUrl = "https://music.example.com:8443/" });

        Assert.Equal("music.example.com", rpId);
        Assert.Contains("https://music.example.com:8443", origins);
    }

    [Fact]
    public void RelyingParty_falls_back_to_localhost_without_config()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions(), new FrontendOptions());

        Assert.Equal("localhost", rpId);
        Assert.NotEmpty(origins);
    }

    [Fact]
    public void RelyingParty_explicit_overrides_win()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions { RpId = "example.com", Origins = ["https://a.example.com"] },
            new FrontendOptions { PublicBaseUrl = "https://b.example.com" });

        Assert.Equal("example.com", rpId);
        Assert.Contains("https://a.example.com", origins);
        Assert.Contains("https://b.example.com", origins);
    }

    private static async Task<object?> Invoke(CurrentUser? currentUser)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(currentUser));
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        var filter = new RequireOwnerFilter();
        var ctx = EndpointFilterInvocationContext.Create(httpContext);
        return await filter.InvokeAsync(ctx, _ => ValueTask.FromResult<object?>("next"));
    }

    // RequireOwnerFilter returns Results.Json(..., statusCode) for rejections; surface the code.
    private static object GetStatusCode(object? result)
    {
        Assert.NotNull(result);
        var prop = result!.GetType().GetProperty("StatusCode");
        Assert.NotNull(prop);
        return prop!.GetValue(result)!;
    }
}
