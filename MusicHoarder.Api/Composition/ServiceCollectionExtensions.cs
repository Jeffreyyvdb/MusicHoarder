using System.IO.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Snapshots;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Version;

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
            .AddOptions<QualityGradingOptions>()
            .BindConfiguration(QualityGradingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

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

        services
            .AddOptions<WebAuthnOptions>()
            .BindConfiguration(WebAuthnOptions.SectionName);

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

        // FIDO2 relying-party config is derived from the frontend's public origin (which Aspire
        // injects) unless explicitly overridden in the WebAuthn section. Built lazily so the
        // frontend URL env var is populated by the time the first ceremony runs.
        services.AddSingleton<Fido2NetLib.IFido2>(sp =>
        {
            var webAuthn = sp.GetRequiredService<IOptionsMonitor<WebAuthnOptions>>().CurrentValue;
            var frontend = sp.GetRequiredService<IOptionsMonitor<FrontendOptions>>().CurrentValue;
            var (rpId, origins) = ResolveRelyingParty(webAuthn, frontend);
            return new Fido2NetLib.Fido2(new Fido2NetLib.Fido2Configuration
            {
                ServerDomain = rpId,
                ServerName = webAuthn.RpName,
                Origins = origins,
            });
        });
        services.AddSingleton<IWebAuthnService, WebAuthnService>();

        services.AddSingleton<JobManager>();
        services.AddSingleton<DirectoryAvailabilityMonitor>();
        services.AddSingleton<IDirectoryAvailability>(sp => sp.GetRequiredService<DirectoryAvailabilityMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<DirectoryAvailabilityMonitor>());

        // Background poll of the GitHub Releases API (ETag-cached) powering the in-app "update available"
        // banner. The named client carries the User-Agent GitHub requires; ServiceDefaults layers
        // resilience/telemetry onto factory clients automatically.
        services.AddHttpClient(ReleaseUpdateMonitor.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicHoarder-UpdateCheck");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<ReleaseUpdateMonitor>();
        services.AddSingleton<IReleaseUpdateMonitor>(sp => sp.GetRequiredService<ReleaseUpdateMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<ReleaseUpdateMonitor>());
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
        services.AddSingleton<IEnrichmentProvider, DeezerEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, AppleMusicEnrichmentProvider>();
        services.AddSingleton<IEnrichmentProvider, TrackerEnrichmentProvider>();
        services.AddSingleton<YeTrackerCatalogService>();
        services.AddSingleton<IEnrichmentProvider, YeTrackerEnrichmentProvider>();
        services.AddSingleton<EnrichmentPipelineChannel>();
        services.AddSingleton<IRuntimeSettingsService, RuntimeSettingsService>();
        services.AddSingleton<IEnrichmentOrchestrator, EnrichmentOrchestrator>();
        services.AddSingleton<IDestinationPathResolver, DestinationPathResolver>();
        services.AddSingleton<IAlbumIdentityReconciler, AlbumIdentityReconciler>();
        services.AddScoped<IAlbumSplitHealer, AlbumSplitHealer>();
        services.AddSingleton<ICanonicalAlbumConsolidator, CanonicalAlbumConsolidator>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        services.AddSingleton<IEmbeddedPictureReader, TagLibEmbeddedPictureReader>();
        services.AddScoped<ICoverArtResolver, CoverArtResolver>();
        services.AddScoped<IAlbumCoverWriter, AlbumCoverWriter>();

        // Disposable on-disk cache of resized WebP cover thumbnails (derived, regenerable artifacts).
        services.AddSingleton<ICoverThumbnailService>(sp =>
        {
            var dir = ResolveWritableDirectory(
                Environment.GetEnvironmentVariable("Artwork__CoverCacheDir") ?? "/data/cover-thumbs",
                "cover-thumbs");
            return new CoverThumbnailService(dir.FullName, sp.GetRequiredService<ILogger<CoverThumbnailService>>());
        });
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

        // Multi-provider canonical album tracklists (full-album view, missing tracks greyed out).
        services.AddSingleton<IAlbumTracklistProvider, MusicBrainzAlbumTracklistProvider>();
        services.AddSingleton<IAlbumTracklistProvider, SpotifyAlbumTracklistProvider>();
        services.AddSingleton<IAlbumTracklistProvider, DeezerAlbumTracklistProvider>();
        services.AddSingleton<IAlbumTracklistProvider, AppleMusicAlbumTracklistProvider>();
        services.AddHostedService<CanonicalAlbumFetchService>();

        // One-time backfill: populate HasCoverArt + write destination album covers for libraries that
        // were already scanned/built before the artwork feature shipped. Idempotent, marker-gated.
        services.AddHostedService<CoverArtBackfillBackgroundService>();
        services.AddHostedService<IngestRunMonitor>();
        // Per-owner pipeline-quality snapshots, captured when a run finalizes (see IngestRunMonitor /
        // QualityGradingBackgroundService). Scoped — it reads/writes through the request DB scope.
        services.AddScoped<IEnrichmentSnapshotService, EnrichmentSnapshotService>();

        // AI quality grading. The chat client + dossier factory are stateless singletons; the
        // grading service creates its own DB scopes so it works from the background sweep and the
        // request path alike.
        services.AddSingleton<QualityGradingProgressTracker>();
        services.AddSingleton<QualityGradingChannel>();
        services.AddSingleton<IQualityDossierFactory, QualityDossierFactory>();
        services.AddSingleton<IChatCompletionClient>(sp =>
        {
            var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            return new OpenAiCompatibleChatClient(
                httpClient,
                sp.GetRequiredService<IOptionsMonitor<QualityGradingOptions>>(),
                sp.GetRequiredService<ILogger<OpenAiCompatibleChatClient>>());
        });
        services.AddSingleton<IQualityGradingService, QualityGradingService>();
        services.AddHostedService<QualityGradingBackgroundService>();

        // Album reconciliation grading — shares the chat client + QualityGradingOptions above.
        services.AddSingleton<AlbumGradingProgressTracker>();
        services.AddSingleton<AlbumGradingChannel>();
        services.AddSingleton<IAlbumGradingDossierFactory, AlbumGradingDossierFactory>();
        services.AddSingleton<IAlbumGradingService, AlbumGradingService>();
        services.AddHostedService<AlbumGradingBackgroundService>();

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

        services.AddSingleton<ITrackerCatalogService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
            var baseUrl = options.Value.TrackerApiBaseUrl;
            if (!baseUrl.EndsWith('/'))
                baseUrl += "/";
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            httpClient.DefaultRequestHeaders.Add(
                "User-Agent", "MusicHoarder/1.0 (+https://github.com/Jeffreyyvdb/MusicHoarder)");
            var logger = sp.GetRequiredService<ILogger<JuiceWrldTrackerService>>();
            return new JuiceWrldTrackerService(httpClient, options, logger);
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

        services.AddSingleton<IDeezerCatalogService>(sp =>
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var cache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
            var logger = sp.GetRequiredService<ILogger<DeezerCatalogService>>();
            return new DeezerCatalogService(httpClient, cache, options, logger);
        });

        services.AddSingleton<IAppleMusicCatalogService>(sp =>
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var cache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
            var logger = sp.GetRequiredService<ILogger<AppleMusicCatalogService>>();
            return new AppleMusicCatalogService(httpClient, cache, options, logger);
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
        => ResolveWritableDirectory(configuredPath, "dpkeys");

    /// <summary>
    /// Returns <paramref name="configuredPath"/> as a created directory, falling back to a local
    /// <c>&lt;BaseDirectory&gt;/&lt;fallbackName&gt;</c> when the configured path isn't writable (typical on
    /// first dev boot when the mount target doesn't exist yet).
    /// </summary>
    private static DirectoryInfo ResolveWritableDirectory(string configuredPath, string fallbackName)
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
            var fallback = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, fallbackName));
            if (!fallback.Exists) fallback.Create();
            return fallback;
        }
    }

    /// <summary>
    /// Resolves the FIDO2 relying-party id (registrable domain) and allowed origins. Explicit
    /// <see cref="WebAuthnOptions"/> values win; otherwise both are derived from the frontend's
    /// public base URL (the browser origin). Falls back to localhost for a bare API boot.
    /// </summary>
    internal static (string RpId, HashSet<string> Origins) ResolveRelyingParty(
        WebAuthnOptions webAuthn, FrontendOptions frontend)
    {
        var origins = new HashSet<string>(webAuthn.Origins
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);

        Uri? frontendUri = null;
        if (!string.IsNullOrWhiteSpace(frontend.PublicBaseUrl)
            && Uri.TryCreate(frontend.PublicBaseUrl, UriKind.Absolute, out var parsed))
        {
            frontendUri = parsed;
            origins.Add($"{parsed.Scheme}://{parsed.Authority}");
        }

        var rpId = !string.IsNullOrWhiteSpace(webAuthn.RpId)
            ? webAuthn.RpId
            : frontendUri?.Host ?? "localhost";

        // A credential is bound to the registrable domain, so it is equally valid on the apex and
        // its `www.` host — but Fido2 still checks the assertion's full origin against this
        // allow-list. For every configured/derived origin, add its apex⇄`www.` counterpart so a
        // browser hitting either host passes without extra config.
        foreach (var sibling in origins.SelectMany(WwwSiblingOrigins).ToList())
            origins.Add(sibling);

        if (origins.Count == 0)
            origins.Add("https://localhost");

        return (rpId, origins);
    }

    /// <summary>
    /// Yields the apex⇄<c>www.</c> counterpart of <paramref name="origin"/>: a host carrying a
    /// <c>www.</c> prefix maps to the bare apex and vice-versa. Single-label hosts (e.g.
    /// <c>localhost</c>) and IP literals are skipped — they have no meaningful <c>www.</c> sibling.
    /// </summary>
    private static IEnumerable<string> WwwSiblingOrigins(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            yield break;
        if (uri.HostNameType is not UriHostNameType.Dns || !uri.Host.Contains('.'))
            yield break;

        var siblingHost = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host["www.".Length..]
            : $"www.{uri.Host}";

        // Preserve a non-default port; drop the implicit one so the origin string stays canonical.
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        yield return $"{uri.Scheme}://{siblingHost}{port}";
    }
}
