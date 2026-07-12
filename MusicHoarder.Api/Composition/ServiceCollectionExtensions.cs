using System.Diagnostics.Metrics;
using System.IO.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.CoverArtArchive;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Observability;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Snapshots;
using MusicHoarder.Api.Soulseek;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.StreamingFlac;
using MusicHoarder.Api.Sync;
using MusicHoarder.Api.Version;
using MusicHoarder.Api.Wishlist;

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
        services.AddSingleton<IValidateOptions<MusicEnricherOptions>, MusicEnricherOptionsValidator>();

        services
            .AddOptions<FrontendOptions>()
            .BindConfiguration(FrontendOptions.SectionName);

        services
            .AddOptions<QualityGradingOptions>()
            .BindConfiguration(QualityGradingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<QualityGradingOptions>, QualityGradingOptionsValidator>();

        services
            .AddOptions<LyricsTranscriptionOptions>()
            .BindConfiguration(LyricsTranscriptionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<SlskdOptions>()
            .BindConfiguration(SlskdOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<StreamingFlacOptions>()
            .BindConfiguration(StreamingFlacOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StreamingFlacOptions>, StreamingFlacOptionsValidator>();

        services
            .AddOptions<SyncOptions>()
            .BindConfiguration(SyncOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>();

        services
            .AddOptions<NavidromeOptions>()
            .BindConfiguration(NavidromeOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NavidromeOptions>, NavidromeOptionsValidator>();

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
        services.AddSingleton<DownloadProgressTracker>();
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
        // Pipeline-domain OTel metrics. Built after the channel so the queue-depth gauge can observe its
        // live in-flight count (one-way: the channel never depends on metrics).
        services.AddSingleton<PipelineMetrics>(sp =>
        {
            var metrics = new PipelineMetrics(sp.GetRequiredService<IMeterFactory>());
            var channel = sp.GetRequiredService<EnrichmentPipelineChannel>();
            metrics.SetQueueDepthProvider(() => channel.InFlight);
            return metrics;
        });
        services.AddSingleton<IRuntimeSettingsService, RuntimeSettingsService>();
        services.AddSingleton<IEnrichmentOrchestrator, EnrichmentOrchestrator>();
        services.AddSingleton<IDestinationPathResolver, DestinationPathResolver>();
        services.AddSingleton<IAlbumIdentityReconciler, AlbumIdentityReconciler>();
        services.AddScoped<IAlbumSplitHealer, AlbumSplitHealer>();
        services.AddScoped<IArtistCreditHealer, ArtistCreditHealer>();
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

        // Wishlist downloader: fetches Pending wishlist items into the source tree, then the scanner
        // ingests them like any other file. Ordered provider chain (MusicEnricher:DownloadProviders);
        // an unconfigured slskd reports NotFound so the chain falls through to yt-dlp.
        services.AddSingleton<ISlskdClient>(sp => new SlskdClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<IOptionsMonitor<SlskdOptions>>(),
            sp.GetRequiredService<ILogger<SlskdClient>>()));
        services.AddSingleton<SlskdFileFetcher>();
        // Optional streaming-FLAC acquisition sidecar (spotiflac). Off unless StreamingFlac:SidecarUrl
        // is set — the provider then reports NotFound so the chain falls through. Infinite client
        // timeout: an /acquire call can take minutes, so the per-call linked CTS owns the deadline.
        services.AddSingleton<StreamingFlacSidecarClient>(sp => new StreamingFlacSidecarClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IOptionsMonitor<StreamingFlacOptions>>(),
            sp.GetRequiredService<ILogger<StreamingFlacSidecarClient>>()));
        // yt-dlp stays first: ResolveProviders falls back to the first registered provider when the
        // configured chain resolves to nothing, and that fallback has always been yt-dlp. yt-dlp is
        // download-only (it can't produce lossless, so it's not an IUpgradeProvider). slskd + spotiflac
        // register their single instance under BOTH interfaces so the download chain and the upgrade
        // chain share one object.
        services.AddSingleton<IDownloadProvider, YtDlpDownloadProvider>();
        services.AddSingleton<SlskdDownloadProvider>();
        services.AddSingleton<IDownloadProvider>(sp => sp.GetRequiredService<SlskdDownloadProvider>());
        services.AddSingleton<IUpgradeProvider>(sp => sp.GetRequiredService<SlskdDownloadProvider>());
        services.AddSingleton<StreamingFlacDownloadProvider>();
        services.AddSingleton<IDownloadProvider>(sp => sp.GetRequiredService<StreamingFlacDownloadProvider>());
        services.AddSingleton<IUpgradeProvider>(sp => sp.GetRequiredService<StreamingFlacDownloadProvider>());
        services.AddScoped<WishlistDownloadProcessor>();
        services.AddHostedService<DownloadBackgroundService>();
        // Single-track URL import: resolves a pasted YouTube video's metadata via a yt-dlp probe.
        services.AddSingleton<Import.IYouTubeMetadataResolver, Import.YouTubeMetadataResolver>();

        // Quality upgrades (manual + automatic): search/download worker over the IUpgradeProvider
        // chain, the merge sweep that swaps a verified better file into the target row (Id-preserving),
        // and the periodic auto-sweep that queues lossy library tracks.
        services.AddSingleton<QualityUpgradeChannel>();
        services.AddScoped<QualityUpgradeService>();
        services.AddScoped<UpgradeMergeService>();
        services.AddScoped<AutomaticUpgradeSweep>();
        services.AddHostedService<QualityUpgradeBackgroundService>();

        // Instance sync (receive side): ingest applies pushed tracks; the endpoint filter is the
        // machine-to-machine auth gate. Both are inert unless Sync:Mode=Receive.
        services.AddScoped<ISyncIngestService, SyncIngestService>();
        services.AddSingleton<SyncApiKeyFilter>();

        // Instance sync (push side): the builder's post-build hook feeds the channel; the worker
        // checks-then-uploads with a persistent outbox (TrackSyncState). Inert unless Sync:Mode=Push.
        services.AddSingleton<TrackSyncChannel>();
        services.AddSingleton<ITrackSyncEnqueuer, TrackSyncEnqueuer>();
        services.AddSingleton<ISyncPushClient>(sp => new SyncPushClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, // per-call CTS owns timeouts (uploads are long)
            sp.GetRequiredService<IOptionsMonitor<SyncOptions>>(),
            sp.GetRequiredService<ILogger<SyncPushClient>>()));
        services.AddScoped<TrackSyncProcessor>();
        services.AddHostedService<TrackSyncBackgroundService>();

        // Two-way like/favorite sync with Navidrome (Subsonic star/unstar). Inert unless configured.
        // The Subsonic client uses an infinite HttpClient timeout; per-call CTS owns the deadline.
        services.AddSingleton<INavidromeClient>(sp => new NavidromeSubsonicClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IOptionsMonitor<NavidromeOptions>>(),
            sp.GetRequiredService<ILogger<NavidromeSubsonicClient>>()));
        services.AddSingleton<NavidromeLikeSyncChannel>();
        services.AddSingleton<INavidromeLikeEnqueuer, NavidromeLikeEnqueuer>();
        services.AddScoped<NavidromeLikeReconciler>();
        services.AddHostedService<NavidromeLikeSyncBackgroundService>();

        // Multi-provider canonical album tracklists (full-album view, missing tracks greyed out).
        services.AddSingleton<IAlbumTracklistProvider, MusicBrainzAlbumTracklistProvider>();
        services.AddSingleton<IAlbumTracklistProvider, SpotifyAlbumTracklistProvider>();
        services.AddSingleton<IAlbumTracklistProvider, DeezerAlbumTracklistProvider>();
        services.AddSingleton<IAlbumTracklistProvider, AppleMusicAlbumTracklistProvider>();
        services.AddHostedService<CanonicalAlbumFetchService>();

        services.AddHostedService<ExternalCoverArtSweepBackgroundService>();
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
            .AddCheck<LibraryDirectoriesHealthCheck>("library-directories", tags: ["pipeline"])
            .AddCheck<SpotifyTokenRefreshHealthCheck>("spotify-token-refresh", tags: ["pipeline"]);

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

        // Experimental AI lyrics transcription (OpenAI-compatible /audio/transcriptions). Infinite
        // HttpClient timeout — the service bounds each call itself via LyricsTranscriptionOptions.TimeoutSeconds.
        // The aligner calls OpenRouter (same creds as QualityGrading) with its own fast LlmModel + reasoning off.
        services.AddSingleton<LlmLyricsAligner>(sp => new LlmLyricsAligner(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IOptionsMonitor<QualityGradingOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<LyricsTranscriptionOptions>>(),
            sp.GetRequiredService<ILogger<LlmLyricsAligner>>()));
        services.AddSingleton<ILyricsTranscriptionService>(sp =>
        {
            var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            return new LyricsTranscriptionService(
                httpClient,
                sp.GetRequiredService<ILrcLibService>(),
                sp.GetRequiredService<LlmLyricsAligner>(),
                sp.GetRequiredService<IOptionsMonitor<LyricsTranscriptionOptions>>(),
                sp.GetRequiredService<IOptions<MusicEnricherOptions>>(),
                sp.GetRequiredService<ILogger<LyricsTranscriptionService>>());
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

        services.AddSingleton<ICoverArtArchiveClient>(sp =>
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
            var logger = sp.GetRequiredService<ILogger<CoverArtArchiveClient>>();
            return new CoverArtArchiveClient(httpClient, options, logger);
        });

        services.AddSingleton<IExternalCoverArtFetcher>(sp =>
        {
            // Plain image-CDN downloads (Deezer/iTunes cover URLs) — no per-provider base address.
            var imageHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            imageHttpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)");
            return new ExternalCoverArtFetcher(
                sp.GetRequiredService<ICoverArtArchiveClient>(),
                sp.GetRequiredService<IDeezerCatalogService>(),
                sp.GetRequiredService<IAppleMusicCatalogService>(),
                imageHttpClient,
                sp.GetRequiredService<IOptions<MusicEnricherOptions>>(),
                sp.GetRequiredService<ILogger<ExternalCoverArtFetcher>>());
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
        services.AddSingleton<SpotifyTokenRefreshHealth>();
        services.AddHostedService<SpotifyTokenRefreshService>();
        services.AddHostedService<SpotifyLibraryMatchBackgroundService>();
        services.AddSingleton<ISpotifyIsrcResolver, SpotifyIsrcResolver>();
        services.AddScoped<IWishlistService, WishlistService>();
        services.AddHostedService<WishlistSyncBackgroundService>();

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
            var navidromeLikeEnqueuer = sp.GetRequiredService<INavidromeLikeEnqueuer>();
            var trackSyncEnqueuer = sp.GetRequiredService<ITrackSyncEnqueuer>();
            var spotifyOptions = sp.GetRequiredService<IOptions<SpotifyOptions>>();
            return new SpotifyLibraryComparisonService(
                spotifyApi, scopeFactory, ownerLookup, navidromeLikeEnqueuer, trackSyncEnqueuer, spotifyOptions, logger);
        });

        // Export Spotify Liked Songs + playlists to on-disk .m3u8 files for Navidrome/Plex/Jellyfin.
        services.AddSingleton<IM3uPlaylistWriter, M3uPlaylistWriter>();
        services.AddScoped<IPlaylistExportService, PlaylistExportService>();
        services.AddHostedService<PlaylistExportBackgroundService>();

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
