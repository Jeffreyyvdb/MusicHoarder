using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Composition;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// Guards the access rules the WebAuthn enrollment/management endpoints rely on
/// (<see cref="RouteHandlerBuilderExtensions.RequireRealAccount"/>, plus
/// <see cref="RouteHandlerBuilderExtensions.RequireOwner"/> which the rest of the API uses) and the
/// relying-party derivation that feeds the FIDO2 configuration.
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
    public async Task RequireRealAccount_rejects_anonymous_with_401()
    {
        var result = await Invoke(currentUser: null, new RequireRealAccountFilter());
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<int>(GetStatusCode(result)));
    }

    [Fact]
    public async Task RequireRealAccount_rejects_demo_with_403()
    {
        // The demo's credentials are shared by every visitor, so it never enrols anything.
        var result = await Invoke(TestCurrentUserAccessor.DemoUser, new RequireRealAccountFilter());
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<int>(GetStatusCode(result)));
    }

    [Fact]
    public async Task RequireRealAccount_allows_owner_and_friend_through()
    {
        Assert.Equal("next", await Invoke(TestCurrentUserAccessor.OwnerUser, new RequireRealAccountFilter()));
        // A friend enrolling a passkey for their own account is what makes the Android client's
        // passkey sign-in available to them at all.
        Assert.Equal("next", await Invoke(TestCurrentUserAccessor.FriendUser, new RequireRealAccountFilter()));
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

    private static Task<object?> Invoke(CurrentUser? currentUser) =>
        Invoke(currentUser, new RequireOwnerFilter());

    private static async Task<object?> Invoke(CurrentUser? currentUser, IEndpointFilter filter)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(currentUser));
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        var ctx = EndpointFilterInvocationContext.Create(httpContext);
        return await filter.InvokeAsync(ctx, _ => ValueTask.FromResult<object?>("next"));
    }

    // Both filters return Results.Json(..., statusCode) for rejections; surface the code.
    private static object GetStatusCode(object? result)
    {
        Assert.NotNull(result);
        var prop = result!.GetType().GetProperty("StatusCode");
        Assert.NotNull(prop);
        return prop!.GetValue(result)!;
    }
}
