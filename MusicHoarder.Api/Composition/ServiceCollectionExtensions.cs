using System.IO.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMusicHoarderServices(this IServiceCollection services)
    {
        services
            .AddOptions<MusicEnricherOptions>()
            .BindConfiguration(MusicEnricherOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<FrontendOptions>()
            .BindConfiguration(FrontendOptions.SectionName);

        services
            .AddOptions<SpotifyOptions>()
            .BindConfiguration(SpotifyOptions.SectionName);

        services.AddSingleton<JobManager>();
        services.AddSingleton<ScanProgressTracker>();
        services.AddSingleton<FingerprintProgressTracker>();
        services.AddSingleton<EnrichmentProgressTracker>();
        services.AddSingleton<LibraryBuilderProgressTracker>();
        services.AddSingleton<IFpcalcService, FpcalcService>();
        services.AddSingleton<IAcoustIdMatchValidator, AcoustIdMatchValidator>();
        services.AddSingleton<IEnrichmentProvider, AcoustIdEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, MusicBrainzWebEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, SpotifyApiEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, TrackerEnrichmentProvider>();
        services.AddSingleton<EnrichmentPipelineChannel>();
        services.AddSingleton<IEnrichmentOrchestrator, EnrichmentOrchestrator>();
        services.AddSingleton<IDestinationPathResolver, DestinationPathResolver>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddScoped<ILibraryTagWriter, TagLibLibraryTagWriter>();
        services.AddScoped<ILibraryDestinationCleaner, LibraryDestinationCleaner>();
        services.AddScoped<ILibraryBuilderService, LibraryBuilderService>();
        services.AddScoped<IPipelinePurgeService, PipelinePurgeService>();

        services.AddHostedService<ScannerBackgroundService>();
        services.AddHostedService<FingerprintBackgroundService>();
        services.AddHostedService<EnrichmentBackgroundService>();
        services.AddHostedService<LibraryBuilderBackgroundService>();

        services.AddScoped<IFileSystem, FileSystem>();
        services.AddScoped<IFileScanner, FileScanner>();
        services.AddScoped<IIndexService, IndexService>();

        services.AddSingleton<IAcoustIdService>(sp =>
        {
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.acoustid.org/")
            };
            var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
            var logger = sp.GetRequiredService<ILogger<AcoustIdService>>();
            return new AcoustIdService(httpClient, options, logger);
        });

        services.AddSingleton<ILrcLibService>(sp =>
        {
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://lrclib.net/"),
                DefaultRequestHeaders =
                {
                    { "User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)" }
                }
            };
            var logger = sp.GetRequiredService<ILogger<LrcLibService>>();
            return new LrcLibService(httpClient, logger);
        });

        services.AddSingleton<ISpotifyCatalogSearchService>(sp =>
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var cache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
            var logger = sp.GetRequiredService<ILogger<SpotifyCatalogSearchService>>();
            return new SpotifyCatalogSearchService(httpClient, cache, options, logger);
        });

        services.AddSingleton<ISpotifyOAuthService>(sp =>
        {
            var httpClient = new HttpClient();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var spotifyOpts = sp.GetRequiredService<IOptions<SpotifyOptions>>();
            var logger = sp.GetRequiredService<ILogger<SpotifyOAuthService>>();
            return new SpotifyOAuthService(scopeFactory, httpClient, spotifyOpts, logger);
        });
        services.AddHostedService<SpotifyTokenRefreshService>();
        services.AddHostedService<SpotifyLibraryMatchBackgroundService>();

        services.AddMemoryCache();
        services.AddSingleton<ISpotifyApiService>(sp =>
        {
            var httpClient = new HttpClient();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var oauthService = sp.GetRequiredService<ISpotifyOAuthService>();
            var cache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var logger = sp.GetRequiredService<ILogger<SpotifyApiService>>();
            return new SpotifyApiService(scopeFactory, oauthService, httpClient, cache, logger);
        });
        services.AddSingleton<ISpotifyLibraryComparisonService>(sp =>
        {
            var spotifyApi = sp.GetRequiredService<ISpotifyApiService>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = sp.GetRequiredService<ILogger<SpotifyLibraryComparisonService>>();
            return new SpotifyLibraryComparisonService(spotifyApi, scopeFactory, logger);
        });

        return services;
    }
}
