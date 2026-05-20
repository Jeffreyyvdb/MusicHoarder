using System.IO.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Settings;
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

        services
            .AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<ResendOptions>()
            .BindConfiguration(ResendOptions.SectionName);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();
        services.AddSingleton<IOwnerLookupService, OwnerLookupService>();
        services.AddSingleton<ISessionCookieService, SessionCookieService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ConsoleMagicLinkSender>();
        services.AddScoped<RequireOwnerFilter>();

        // Pick the magic-link sender at startup: Resend when an API key is configured, otherwise
        // the console-logging fallback. Registered as a singleton; no Resend → no Resend client.
        services.AddSingleton<IMagicLinkSender>(sp =>
        {
            var resendOpts = sp.GetRequiredService<IOptionsMonitor<ResendOptions>>().CurrentValue;
            if (string.IsNullOrWhiteSpace(resendOpts.ApiKey))
                return sp.GetRequiredService<ConsoleMagicLinkSender>();
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return new ResendMagicLinkSender(
                http,
                sp.GetRequiredService<IOptionsMonitor<ResendOptions>>(),
                sp.GetRequiredService<ConsoleMagicLinkSender>(),
                sp.GetRequiredService<ILogger<ResendMagicLinkSender>>());
        });

        services.AddDataProtection()
            .SetApplicationName("MusicHoarder")
            .PersistKeysToFileSystem(ResolveDataProtectionKeysDirectory(
                Environment.GetEnvironmentVariable("Auth__DataProtectionKeysPath")
                ?? "/data/dpkeys"));

        services.AddSingleton<JobManager>();
        services.AddSingleton<DirectoryAvailabilityMonitor>();
        services.AddSingleton<IDirectoryAvailability>(sp => sp.GetRequiredService<DirectoryAvailabilityMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<DirectoryAvailabilityMonitor>());
        services.AddSingleton<ScanProgressTracker>();
        services.AddSingleton<FingerprintProgressTracker>();
        services.AddSingleton<EnrichmentProgressTracker>();
        services.AddSingleton<LibraryBuilderProgressTracker>();
        services.AddSingleton<PurgeStatusTracker>();
        services.AddSingleton<IFpcalcService, FpcalcService>();
        services.AddSingleton<IAcoustIdMatchValidator, AcoustIdMatchValidator>();
        services.AddSingleton<IEnrichmentProvider, AcoustIdEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, MusicBrainzWebEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, SpotifyApiEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, TrackerEnrichmentProvider>();
        services.AddSingleton<EnrichmentPipelineChannel>();
        services.AddSingleton<IRuntimeSettingsService, RuntimeSettingsService>();
        services.AddSingleton<IEnrichmentOrchestrator, EnrichmentOrchestrator>();
        services.AddSingleton<IDestinationPathResolver, DestinationPathResolver>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddScoped<ILibraryTagWriter, TagLibLibraryTagWriter>();
        services.AddScoped<ILibraryDestinationCleaner, LibraryDestinationCleaner>();
        services.AddScoped<ILibraryBuilderService, LibraryBuilderService>();
        services.AddScoped<IPipelinePurgeService, PipelinePurgeService>();

        // Demo seeder runs first so its data is in place before background workers (which are
        // idempotent — synthetic rows are skipped anyway).
        services.AddHostedService<DemoSeederHostedService>();
        services.AddHostedService<ScannerBackgroundService>();
        services.AddHostedService<FingerprintBackgroundService>();
        services.AddHostedService<EnrichmentBackgroundService>();
        services.AddHostedService<LibraryBuilderBackgroundService>();

        services.AddHealthChecks()
            .AddCheck<LibraryDirectoriesHealthCheck>("library-directories", tags: ["pipeline"]);

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

        services.AddSingleton<IMusicBrainzWebService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://musicbrainz.org/ws/2/"),
                Timeout = TimeSpan.FromSeconds(30),
            };
            httpClient.DefaultRequestHeaders.Add("User-Agent", options.Value.MusicBrainzUserAgent);
            var logger = sp.GetRequiredService<ILogger<MusicBrainzWebService>>();
            return new MusicBrainzWebService(httpClient, options, logger);
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
            var ownerLookup = sp.GetRequiredService<IOwnerLookupService>();
            return new SpotifyOAuthService(scopeFactory, httpClient, ownerLookup, spotifyOpts, logger);
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
            var ownerLookup = sp.GetRequiredService<IOwnerLookupService>();
            return new SpotifyApiService(scopeFactory, oauthService, httpClient, cache, ownerLookup, logger);
        });
        services.AddSingleton<ISpotifyLibraryComparisonService>(sp =>
        {
            var spotifyApi = sp.GetRequiredService<ISpotifyApiService>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = sp.GetRequiredService<ILogger<SpotifyLibraryComparisonService>>();
            var ownerLookup = sp.GetRequiredService<IOwnerLookupService>();
            return new SpotifyLibraryComparisonService(spotifyApi, scopeFactory, ownerLookup, logger);
        });

        return services;
    }

    /// <summary>
    /// Resolves the data-protection keys directory from <see cref="AuthOptions.DataProtectionKeysPath"/>,
    /// falling back to <c>~/.aspnet/DataProtection-Keys</c>-style local directory when the configured
    /// path isn't writable (e.g. when the volume mount hasn't been created yet on first dev boot).
    /// </summary>
    private static DirectoryInfo ResolveDataProtectionKeysDirectory(string configuredPath)
    {
        try
        {
            var dir = new DirectoryInfo(configuredPath);
            if (!dir.Exists)
                dir.Create();
            return dir;
        }
        catch
        {
            // Configured directory not writable (typical on first dev boot when the mount target
            // doesn't exist). Use a local fallback so DP keys still persist across restarts
            // within the same checkout.
            var fallback = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "dpkeys"));
            if (!fallback.Exists) fallback.Create();
            return fallback;
        }
    }
}
