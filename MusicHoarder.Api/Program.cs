using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.Middleware;
using MusicHoarder.Api.Composition;
using MusicHoarder.Api.OpenApi;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Persistence.Interceptors;
using MusicHoarder.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Raise the thread-pool floor before anything else starts. The pipeline workers run synchronous
// TagLib file I/O (FileScanner, TagLibLibraryTagWriter) under high Parallel.ForEachAsync
// parallelism across several concurrent stages, which blocks pool threads. On a low-vCPU host the
// pool grows only ~1 thread/sec, so without a floor those blocked workers crowd out Kestrel
// request continuations (e.g. /api/auth/me) and the UI hangs while CPU stays low. Derive the floor
// from the configured worker concurrency plus headroom for request handling so it scales with the
// concurrency knobs. Values mirror MusicEnricherOptions defaults.
var enricher = builder.Configuration.GetSection("MusicEnricher");
var smbConcurrency = enricher.GetValue("SmbConcurrency", 8);
var fingerprintConcurrency = enricher.GetValue("FingerprintConcurrency", 8);
var libraryBuilderConcurrency = enricher.GetValue("LibraryBuilderWorkerConcurrency", 2);
var minThreads = Math.Max(
    32,
    (smbConcurrency * 2) + fingerprintConcurrency + (libraryBuilderConcurrency * 2) + 8);
ThreadPool.SetMinThreads(minThreads, minThreads);

builder.AddServiceDefaults();

builder.Services.AddMusicHoarderServices();

// Register the DbContext ourselves (non-pooled) so we can use a second constructor that takes
// ICurrentUserAccessor for per-user EF global query filters. Then call EnrichNpgsqlDbContext to
// apply Aspire's connection-string + OpenTelemetry wiring on top.
builder.Services.AddSingleton<RebuildOnMetadataChangeInterceptor>();
builder.Services.AddDbContext<MusicHoarderDbContext>((sp, options) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    options.UseNpgsql(configuration.GetConnectionString("musichoarderdb"));
    options.AddInterceptors(sp.GetRequiredService<RebuildOnMetadataChangeInterceptor>());
});
builder.EnrichNpgsqlDbContext<MusicHoarderDbContext>();

builder.Services.AddOpenApi(options => options.AddDocumentTransformer<CookieSecuritySchemeTransformer>());

var app = builder.Build();

app.MapDefaultEndpoints();

await app.ApplyPendingMigrationsAsync();

// Capability banner: one line summarizing resolved optional-subsystem state, so an operator can see at
// a glance what's actually on (several of these are gated by keys/flags that are easy to misconfigure).
{
    var me = app.Services.GetRequiredService<IOptions<MusicEnricherOptions>>().Value;
    var qg = app.Services.GetRequiredService<IOptions<QualityGradingOptions>>().Value;
    app.Logger.LogInformation(
        "Capabilities — AutoStartPipeline={AutoStart}, SpotifyProvider={Spotify}, AcoustID={AcoustId}, " +
        "WishlistDownloads={Wishlist}(auto={AutoDl}), QualityGrading={Grading}(configured={GradingConfigured}), " +
        "CanonicalAlbumFetch={Canonical}, ExternalCoverArt={Cover}",
        me.AutoStartPipeline,
        me.EnableSpotifyApiProvider,
        !string.IsNullOrWhiteSpace(me.AcoustIdApiKey),
        me.EnableWishlistDownloads, me.AutoDownloadWishlist,
        qg.Enabled, qg.IsConfigured,
        me.EnableCanonicalAlbumFetch,
        me.EnableExternalCoverArtFetch);

    // Not a hard validation failure (the downloader simply idles), but surface it so an operator who
    // enabled downloads notices the missing staging directory rather than wondering why nothing fetches.
    if (me.EnableWishlistDownloads && string.IsNullOrWhiteSpace(me.DownloadDirectory))
        app.Logger.LogWarning(
            "MusicEnricher:EnableWishlistDownloads is on but DownloadDirectory is empty — the download worker will idle until it's set.");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .AddPreferredSecuritySchemes(CookieSecuritySchemeTransformer.SchemeId));
}

// No app-level HTTPS redirection: in deployment TLS terminates at the reverse proxy
// (Traefik/Dokploy) and the API only listens on HTTP, so UseHttpsRedirection can't
// resolve an HTTPS port and just logs "Failed to determine the https port for redirect".

app.UseMiddleware<AuthenticationMiddleware>();
app.UseMiddleware<RequireAuthMiddleware>();
app.UseMiddleware<DemoReadOnlyMiddleware>();

app.MapMusicHoarderEndpoints();

app.Run();
