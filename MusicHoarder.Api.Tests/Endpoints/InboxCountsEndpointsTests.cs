using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// <c>GET /api/inbox/name-counts</c> as the web shell meets it: two numbers under the names it
/// reads, for the account that asked, and for admins only — like the three reports it stands in for.
/// What the numbers are is pinned by <c>InboxNameCounterTests</c>.
/// </summary>
public class InboxCountsEndpointsTests
{
    [Fact]
    public async Task An_admin_gets_both_counts_for_their_own_account()
    {
        var counter = new FakeCounter(new InboxNameCounts(Artists: 6, Albums: 825));

        var (status, body) = await Get(TestCurrentUserAccessor.OwnerUser, counter);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("""{"artists":6,"albums":825}""", body);
        Assert.Equal([TestUsers.OwnerId], counter.AskedFor);
    }

    [Fact]
    public async Task A_member_the_demo_account_and_a_stranger_get_nothing_counted()
    {
        var counter = new FakeCounter(new InboxNameCounts(6, 825));

        Assert.Equal(StatusCodes.Status403Forbidden, (await Get(TestCurrentUserAccessor.FriendUser, counter)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await Get(TestCurrentUserAccessor.DemoUser, counter)).Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await Get(null, counter)).Status);
        Assert.Empty(counter.AskedFor);
    }

    // Through the mapped route rather than the handler, so the admin gate is part of what is tested.
    private static async Task<(int Status, string Body)> Get(CurrentUser? caller, IInboxNameCounter counter)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(counter);
        builder.Services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(caller));
        var app = builder.Build();
        app.MapInboxCountsEndpoints();
        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>());
        Assert.Equal("/api/inbox/name-counts", endpoint.RoutePattern.RawText);
        Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);

        using var scope = app.Services.CreateScope();
        var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = body;
        await endpoint.RequestDelegate!(http);
        return (http.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class FakeCounter(InboxNameCounts counts) : IInboxNameCounter
    {
        public List<Guid> AskedFor { get; } = [];

        public Task<InboxNameCounts> CountAsync(Guid ownerUserId, CancellationToken ct = default)
        {
            AskedFor.Add(ownerUserId);
            return Task.FromResult(counts);
        }
    }
}
