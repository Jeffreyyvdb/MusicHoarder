using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// The demo account may read the admin pages whose data is only ever its own rows (History,
/// Artist names, Share links, Wishlist), and nothing else; a member gets none of them. Runs each
/// route's real request delegate, so the filters are the ones the route table actually wires.
///
/// <para>
/// Services are dummies: a caller the filter rejects never reaches the handler, and a caller it
/// admits reaches a handler that cannot run. Binding does not depend on the caller, so a route that
/// answers a member 403 cleanly has bound its arguments, and anything other than that 403 for the
/// demo on the same route means the filter let it through.
/// </para>
/// </summary>
public class DemoReadAccessTests
{
    /// <summary>The reads the demo account may make: per-user-filtered rows only.</summary>
    private static readonly string[] DemoRoutes =
    [
        "/api/history",
        "/api/library/artists/duplicates",
        "/api/library/dedup/actions",
        "/api/shares",
        "/api/shares/stats",
        "/api/shares/{id:int}/stats",
        "/api/wishlist",
        "/api/wishlist/sources",
    ];

    public static TheoryData<string> DemoReadableRoutes => new(DemoRoutes);

    /// <summary>
    /// Reads behind pages the demo account no longer sees, because they are instance-wide or
    /// documented admin-only. They must stay closed to it.
    /// </summary>
    public static TheoryData<string> AdminOnlyReads => new()
    {
        "/api/albums/quality/overview",
        "/api/quality/trial/status",
        "/api/quality/trial/labels/queue",
        "/api/quality/trial/report",
        "/api/snapshots",
        "/api/playlist-sync",
        "/api/enrichment/split-albums",
        "/api/library/albums/duplicates",
        "/api/quality/progress",
        "/api/inbox/name-counts",
    };

    [Theory]
    [MemberData(nameof(DemoReadableRoutes))]
    public async Task Demo_may_read_its_own_rows(string route)
    {
        var (status, error) = await Invoke(TestCurrentUserAccessor.DemoUser, "GET", route);
        Assert.False(status == StatusCodes.Status403Forbidden, $"demo got 403 ({error}) on {route}");
    }

    [Theory]
    [MemberData(nameof(DemoReadableRoutes))]
    [MemberData(nameof(AdminOnlyReads))]
    public async Task Member_is_still_refused(string route)
    {
        var (status, error) = await Invoke(TestCurrentUserAccessor.FriendUser, "GET", route);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("admin_required", error);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyReads))]
    public async Task Demo_is_refused_instance_wide_reads(string route)
    {
        var (status, error) = await Invoke(TestCurrentUserAccessor.DemoUser, "GET", route);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("admin_required", error);
    }

    [Theory]
    [MemberData(nameof(DemoReadableRoutes))]
    public async Task Admin_still_reads(string route)
    {
        var (status, _) = await Invoke(TestCurrentUserAccessor.OwnerUser, "GET", route);
        Assert.NotEqual(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public void Only_the_vetted_reads_carry_the_demo_marker()
    {
        var marked = Endpoints()
            .Where(e => e.Metadata.GetMetadata<DemoReadable>() is not null)
            .Select(e => $"{Methods(e)} {Normalize(e.RoutePattern.RawText!)}")
            .Order()
            .ToList();

        Assert.Equal(
            DemoRoutes.Select(r => $"GET {Normalize(r)}").Order().ToList(),
            marked);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task The_marker_never_admits_a_write(string method)
    {
        var endpoint = Endpoint("GET", "/api/shares/stats");
        var (status, error) = await RunFilter(TestCurrentUserAccessor.DemoUser, method, endpoint);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("admin_required", error);
    }

    [Fact]
    public async Task The_marker_admits_the_demo_on_a_read()
    {
        var endpoint = Endpoint("GET", "/api/shares/stats");
        Assert.Null((await RunFilter(TestCurrentUserAccessor.DemoUser, "GET", endpoint)).Status);
        Assert.Null((await RunFilter(TestCurrentUserAccessor.DemoUser, "HEAD", endpoint)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden,
            (await RunFilter(TestCurrentUserAccessor.FriendUser, "GET", endpoint)).Status);
    }

    // --- Helpers -----------------------------------------------------------------------------

    private static string Normalize(string pattern) => pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;

    private static string Methods(RouteEndpoint e) =>
        string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []);

    private static List<RouteEndpoint>? _endpoints;

    private static List<RouteEndpoint> Endpoints()
    {
        if (_endpoints is not null) return _endpoints;
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        var routes = new TestRouteBuilder(app.Services);
        routes.MapHistoryEndpoints();
        routes.MapArtistDedupEndpoints();
        routes.MapAlbumDedupEndpoints();
        routes.MapDedupActionsEndpoints();
        routes.MapSharesEndpoints();
        routes.MapWishlistEndpoints();
        routes.MapAlbumQualityEndpoints();
        routes.MapQualityTrialEndpoints();
        routes.MapQualityEndpoints();
        routes.MapSnapshotsEndpoints();
        routes.MapPlaylistSyncEndpoints();
        routes.MapEnrichmentEndpoints();
        routes.MapInboxCountsEndpoints();
        return _endpoints = routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static RouteEndpoint Endpoint(string method, string route) =>
        Endpoints().SingleOrDefault(e =>
            Normalize(e.RoutePattern.RawText!) == Normalize(route)
            && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method))
        ?? throw new InvalidOperationException($"No {method} {route} in the route table.");

    private static async Task<(int? Status, string? Error)> Invoke(CurrentUser caller, string method, string route)
    {
        var endpoint = Endpoint(method, route);
        var http = NewContext(caller, method, endpoint);
        http.Request.RouteValues["id"] = "1";
        try
        {
            await endpoint.RequestDelegate!(http);
        }
        catch (Exception)
        {
            // The handler ran on dummy services: the filter let the caller through.
            return (null, null);
        }

        return (http.Response.StatusCode, ErrorFrom(http));
    }

    private static async Task<(int? Status, string? Error)> RunFilter(CurrentUser caller, string method, Endpoint endpoint)
    {
        var http = NewContext(caller, method, endpoint);
        var filter = new RequireCapabilityFilter(Capability.Administer, "admin_required");
        var result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("next"));
        if (result is "next") return (null, null);
        var status = result?.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        var value = result?.GetType().GetProperty("Value")?.GetValue(result);
        return (status, value?.GetType().GetProperty("error")?.GetValue(value) as string);
    }

    private static DefaultHttpContext NewContext(CurrentUser caller, string method, Endpoint endpoint)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(caller));
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = new DummyServices(services.BuildServiceProvider()) };
        http.Request.Method = method;
        http.Response.Body = new MemoryStream();
        http.SetEndpoint(endpoint);
        return http;
    }

    private static string? ErrorFrom(HttpContext http)
    {
        var body = (MemoryStream)http.Response.Body;
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        if (text.Length == 0) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
    }

    /// <summary>
    /// Resolves what is registered and hands out an inert stand-in for everything else, so a
    /// handler's service parameters bind without the services behind them.
    /// </summary>
    private sealed class DummyServices(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            var real = inner.GetService(serviceType);
            if (real is not null) return real;
            if (serviceType.IsInterface)
                return DispatchProxy.Create(serviceType, typeof(ThrowingProxy));
            if (serviceType.IsClass && !serviceType.IsAbstract)
                return RuntimeHelpers.GetUninitializedObject(serviceType);
            return null;
        }
    }

    public class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Dummy service used: the handler was reached.");
    }

    private sealed class TestRouteBuilder(IServiceProvider inner) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = new AnyServiceProvider(inner);
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotSupportedException();
    }

    private sealed class AnyServiceProvider(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? new AnyServiceIsService() : inner.GetService(serviceType);
    }

    private sealed class AnyServiceIsService : IServiceProviderIsService
    {
        // Classes and interfaces are services; strings, numbers and dates bind from the request.
        public bool IsService(Type serviceType) =>
            (serviceType.IsInterface || serviceType.IsClass) && serviceType != typeof(string) && !serviceType.IsArray;
        public bool IsKeyedService(Type serviceType, object? serviceKey) => true;
    }
}
