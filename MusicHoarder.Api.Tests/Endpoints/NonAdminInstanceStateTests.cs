using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Host directory paths, pipeline run state and grading run state are for admins only: demo and
/// member callers get neither the configured directories nor the run counters or error text.
/// </summary>
public class NonAdminInstanceStateTests
{
    private const string SourceDir = "/srv/dummy/source-music";
    private const string DestDir = "/srv/dummy/built-library";
    private const string ProviderText = "DUMMY-PROVIDER-TEXT";

    public static TheoryData<string> NonAdmins => new() { "demo", "member" };

    private static CurrentUser Caller(string who) => who switch
    {
        "admin" => TestCurrentUserAccessor.OwnerUser,
        "demo" => TestCurrentUserAccessor.DemoUser,
        _ => TestCurrentUserAccessor.FriendUser,
    };

    [Theory]
    [MemberData(nameof(NonAdmins))]
    public async Task Non_admin_gets_no_directory_paths_or_run_state(string who)
    {
        foreach (var path in new[]
                 {
                     "/api/enrichment/library-availability", "/overview",
                     "/library/directory-tree", "/api/quality/progress",
                 })
        {
            var (status, body) = await Get(Caller(who), path);
            Assert.DoesNotContain(SourceDir, body);
            Assert.DoesNotContain(DestDir, body);
            Assert.DoesNotContain(ProviderText, body);
            Assert.DoesNotContain("7654", body);
            if (path != "/api/quality/progress")
                Assert.Equal(StatusCodes.Status200OK, status);
            else
                Assert.Equal(StatusCodes.Status403Forbidden, status);
        }
    }

    [Fact]
    public async Task Non_admin_still_gets_reachability_flags()
    {
        var (_, body) = await Get(Caller("member"), "/api/enrichment/library-availability");
        Assert.Contains("\"sourceAvailable\":true", body);
        Assert.Contains("\"destinationAvailable\":true", body);
    }

    [Fact]
    public async Task Admin_gets_directory_paths_and_run_state()
    {
        Assert.Contains(SourceDir, (await Get(Caller("admin"), "/api/enrichment/library-availability")).Body);
        var overview = (await Get(Caller("admin"), "/overview")).Body;
        Assert.Contains(SourceDir, overview);
        Assert.Contains(DestDir, overview);
        Assert.Contains("7654", overview);
        Assert.Contains(SourceDir, (await Get(Caller("admin"), "/library/directory-tree")).Body);
        var progress = await Get(Caller("admin"), "/api/quality/progress");
        Assert.Equal(StatusCodes.Status200OK, progress.Status);
        Assert.Contains(ProviderText, progress.Body);
    }

    private static async Task<(int Status, string Body)> Get(CurrentUser caller, string path)
    {
        var accessor = new TestCurrentUserAccessor(caller);
        var dbOptions = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

        var scan = new ScanProgressTracker();
        scan.Start(Guid.NewGuid(), 7654);
        var grading = new QualityGradingProgressTracker();
        grading.StartCycle(Guid.NewGuid(), 4321);
        grading.RecordError("out_of_credits", ProviderText);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<ICurrentUserAccessor>(accessor);
        builder.Services.AddScoped(_ => new MusicHoarderDbContext(dbOptions, accessor));
        builder.Services.AddSingleton<IOptions<MusicEnricherOptions>>(Microsoft.Extensions.Options.Options.Create(
            new MusicEnricherOptions { SourceDirectory = SourceDir, DestinationDirectory = DestDir }));
        builder.Services.AddSingleton(scan);
        builder.Services.AddSingleton(new FingerprintProgressTracker());
        builder.Services.AddSingleton(new EnrichmentProgressTracker());
        builder.Services.AddSingleton(grading);
        builder.Services.AddSingleton<IDirectoryAvailability>(new FixedAvailability());
        builder.Services.AddSingleton<IRuntimeSettingsService>(new StubRuntimeSettings());
        builder.Services.AddSingleton<IOptionsMonitor<QualityGradingOptions>>(new StubQualityOptions());
        var app = builder.Build();
        // The route tables hold many handlers with unrelated dependencies; only the requested route runs.
        var routes = new TestRouteBuilder(app.Services);
        routes.MapDashboardEndpoints();
        routes.MapEnrichmentEndpoints();
        routes.MapQualityEndpoints();

        var endpoint = routes.DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == path
                         && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));

        using var scope = app.Services.CreateScope();
        var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = body;
        await endpoint.RequestDelegate!(http);
        return (http.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class StubRuntimeSettings : IRuntimeSettingsService
    {
        private static readonly EffectiveSettings Settings = new(
            true, true, true, true, true, true,
            QualityGradingEnabled: true, AutoDownloadWishlist: false, AlbumCompletionEnabled: false,
            ReleaseStagedSourcesEnabled: false, UpdatedAtUtc: null);

        public Task<EffectiveSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(Settings);
        public Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default) => Task.FromResult(Settings);
    }

    private sealed class StubQualityOptions : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = new();
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
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
        public bool IsService(Type serviceType) => true;
        public bool IsKeyedService(Type serviceType, object? serviceKey) => true;
    }

    private sealed class FixedAvailability : IDirectoryAvailability
    {
        public DirectoryAvailabilitySnapshot Current { get; } =
            new(true, true, SourceDir, DestDir, DateTime.UtcNow);

        public Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }
}
