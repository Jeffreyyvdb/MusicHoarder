using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("", async (
                IRuntimeSettingsService runtimeSettings,
                IOptions<MusicEnricherOptions> options,
                IOptions<SpotifyOptions> spotifyOptions,
                CancellationToken ct) =>
            {
                var effective = await runtimeSettings.GetAsync(ct);
                var opts = options.Value;
                return Results.Ok(new SettingsResponse(
                    Paths: new PathsView(
                        SourceDirectory: opts.SourceDirectory,
                        DestinationDirectory: opts.DestinationDirectory,
                        FpcalcPath: opts.FpcalcPath),
                    Providers: new ProvidersView(
                        AcoustId: effective.EnableAcoustIdProvider,
                        MusicBrainzWeb: effective.EnableMusicBrainzWebProvider,
                        SpotifyApi: effective.EnableSpotifyApiProvider,
                        Tracker: effective.EnableTrackerProvider),
                    Pipeline: new PipelineView(
                        SpotifyApiMatchedThreshold: effective.SpotifyApiMatchedThreshold,
                        AcoustIdScoreThreshold: effective.AcoustIdScoreThreshold,
                        EnrichmentWorkerConcurrency: effective.EnrichmentWorkerConcurrency,
                        LibraryBuilderWorkerConcurrency: effective.LibraryBuilderWorkerConcurrency),
                    Spotify: new SpotifyView(
                        OAuthRedirectBaseUrl: spotifyOptions.Value.OAuthRedirectBaseUrl,
                        Scopes: SpotifyScopes),
                    UpdatedAtUtc: effective.UpdatedAtUtc));
            })
            .WithName("GetSettings")
            .WithSummary("Returns the effective runtime settings (paths, provider toggles, pipeline tuning, Spotify metadata).");

        group.MapPut("", async (
                SettingsUpdateRequest request,
                IRuntimeSettingsService runtimeSettings,
                CancellationToken ct) =>
            {
                if (request is null)
                    return Results.BadRequest(new { message = "Request body required." });

                if (request.Pipeline?.SpotifyApiMatchedThreshold is { } sp && (sp < 0.0 || sp > 1.0))
                    return Results.BadRequest(new { message = "SpotifyApiMatchedThreshold must be in 0..1." });

                if (request.Pipeline?.AcoustIdScoreThreshold is { } ac && (ac < 0.0 || ac > 1.0))
                    return Results.BadRequest(new { message = "AcoustIdScoreThreshold must be in 0..1." });

                if (request.Pipeline?.EnrichmentWorkerConcurrency is { } ew && (ew < 1 || ew > 64))
                    return Results.BadRequest(new { message = "EnrichmentWorkerConcurrency must be in 1..64." });

                if (request.Pipeline?.LibraryBuilderWorkerConcurrency is { } lw && (lw < 1 || lw > 64))
                    return Results.BadRequest(new { message = "LibraryBuilderWorkerConcurrency must be in 1..64." });

                var update = new RuntimeSettingsUpdate
                {
                    EnableAcoustIdProvider = request.Providers?.AcoustId,
                    EnableMusicBrainzWebProvider = request.Providers?.MusicBrainzWeb,
                    EnableSpotifyApiProvider = request.Providers?.SpotifyApi,
                    EnableTrackerProvider = request.Providers?.Tracker,
                    SpotifyApiMatchedThreshold = request.Pipeline?.SpotifyApiMatchedThreshold,
                    AcoustIdScoreThreshold = request.Pipeline?.AcoustIdScoreThreshold,
                    EnrichmentWorkerConcurrency = request.Pipeline?.EnrichmentWorkerConcurrency,
                    LibraryBuilderWorkerConcurrency = request.Pipeline?.LibraryBuilderWorkerConcurrency,
                };

                var effective = await runtimeSettings.UpdateAsync(update, ct);
                return Results.Ok(new
                {
                    providers = new ProvidersView(
                        effective.EnableAcoustIdProvider,
                        effective.EnableMusicBrainzWebProvider,
                        effective.EnableSpotifyApiProvider,
                        effective.EnableTrackerProvider),
                    pipeline = new PipelineView(
                        effective.SpotifyApiMatchedThreshold,
                        effective.AcoustIdScoreThreshold,
                        effective.EnrichmentWorkerConcurrency,
                        effective.LibraryBuilderWorkerConcurrency),
                    updatedAtUtc = effective.UpdatedAtUtc,
                });
            })
            .WithName("UpdateSettings")
            .WithSummary("Updates the persisted runtime settings overlay. Provider toggles take effect on the next enrichment cycle; worker-concurrency changes are persisted but applied on next API restart.")
            .RequireOwner();

        return app;
    }

    private static readonly string[] SpotifyScopes =
    [
        "user-library-read",
        "playlist-read-private",
        "playlist-read-collaborative",
        "user-top-read",
    ];
}

public sealed record SettingsResponse(
    PathsView Paths,
    ProvidersView Providers,
    PipelineView Pipeline,
    SpotifyView Spotify,
    DateTime? UpdatedAtUtc);

public sealed record PathsView(string SourceDirectory, string DestinationDirectory, string FpcalcPath);
public sealed record ProvidersView(bool AcoustId, bool MusicBrainzWeb, bool SpotifyApi, bool Tracker);
public sealed record PipelineView(
    double SpotifyApiMatchedThreshold,
    double AcoustIdScoreThreshold,
    int EnrichmentWorkerConcurrency,
    int LibraryBuilderWorkerConcurrency);
public sealed record SpotifyView(string OAuthRedirectBaseUrl, IReadOnlyList<string> Scopes);

public sealed record SettingsUpdateRequest(ProvidersUpdate? Providers, PipelineUpdate? Pipeline);
public sealed record ProvidersUpdate(bool? AcoustId, bool? MusicBrainzWeb, bool? SpotifyApi, bool? Tracker);
public sealed record PipelineUpdate(
    double? SpotifyApiMatchedThreshold,
    double? AcoustIdScoreThreshold,
    int? EnrichmentWorkerConcurrency,
    int? LibraryBuilderWorkerConcurrency);
