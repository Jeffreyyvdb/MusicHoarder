using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Spotify;

/// <summary>
/// GET /api/spotify/callback rewrites the owner-global Spotify tokens, so it must be
/// admin-only like /connect, /disconnect and PUT /credentials. A member or the demo account must
/// never reach the code exchange.
/// </summary>
public class SpotifyCallbackAuthorizationTests
{
    [Fact]
    public async Task Callback_rejects_member_and_demo_without_exchanging_the_code()
    {
        foreach (var user in new[] { TestCurrentUserAccessor.FriendUser, TestCurrentUserAccessor.DemoUser })
        {
            var oauth = new RecordingOAuth();
            var (status, body) = await InvokeCallbackAsync(user, oauth);

            Assert.Equal(StatusCodes.Status403Forbidden, status);
            Assert.Contains("admin_required", body);
            Assert.Empty(oauth.ExchangedCodes);
        }
    }

    [Fact]
    public async Task Callback_still_exchanges_the_code_for_an_admin()
    {
        var oauth = new RecordingOAuth();
        var (status, _) = await InvokeCallbackAsync(TestCurrentUserAccessor.OwnerUser, oauth);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(["DUMMY_CODE"], oauth.ExchangedCodes);
    }

    private static async Task<(int Status, string Body)> InvokeCallbackAsync(CurrentUser user, RecordingOAuth oauth)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<ISpotifyOAuthService>(oauth);
        // Other routes in the group only need their service types to be known so parameter binding resolves.
        builder.Services.AddSingleton<ISpotifyApiService>(_ => null!);
        builder.Services.AddSingleton<ISpotifyLibraryComparisonService>(_ => null!);
        builder.Services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(user));
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new FrontendOptions()));
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SpotifyOptions()));
        await using var app = builder.Build();
        ((IEndpointRouteBuilder)app).MapSpotifyEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/spotify/callback");

        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.QueryString = new QueryString("?code=DUMMY_CODE");
        ctx.Request.Headers["X-Spotify-Callback-Mode"] = "json";
        ctx.SetEndpoint(endpoint);
        ctx.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    private sealed class RecordingOAuth : ISpotifyOAuthService
    {
        public List<string> ExchangedCodes { get; } = [];

        public Task<SpotifyTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
        {
            ExchangedCodes.Add(code);
            return Task.FromResult(new SpotifyTokenResult(true));
        }

        public Task<SpotifyConnectResult> GetAuthorizationUrlAsync(string redirectUri, string? returnOrigin = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SpotifyTokenResult> RefreshAccessTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SpotifyStatusResult> GetStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveCredentialsAsync(string clientId, string clientSecret, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SpotifyCredentialsResult> GetCredentialsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsureValidTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
