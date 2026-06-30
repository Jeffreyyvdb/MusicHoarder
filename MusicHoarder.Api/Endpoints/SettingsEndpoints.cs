using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
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
                IOptions<QualityGradingOptions> qualityOptions,
                IOptions<LyricsTranscriptionOptions> lyricsTranscriptionOptions,
                ICurrentUserAccessor currentUser,
                CancellationToken ct) =>
            {
                var effective = await runtimeSettings.GetAsync(ct);
                var opts = options.Value;
                // Configured paths are host-level (not tenant-scoped). On a public instance a demo
                // user must not learn the owner's filesystem layout, so blank them for non-owners.
                // Provider toggles and pipeline tuning are harmless to surface.
                var isOwner = currentUser.User?.IsOwner == true;
                var q = qualityOptions.Value;
                // "Configured" = a key + base URL exist on the server, independent of the runtime
                // Enabled toggle — lets the UI tell "you turned it off" apart from "no key set".
                var qualityConfigured = !string.IsNullOrWhiteSpace(q.ApiKey) && !string.IsNullOrWhiteSpace(q.BaseUrl);
                return Results.Ok(new SettingsResponse(
                    Paths: isOwner
                        ? new PathsView(
                            SourceDirectory: opts.SourceDirectory,
                            DestinationDirectory: opts.DestinationDirectory,
                            FpcalcPath: opts.FpcalcPath)
                        : new PathsView(string.Empty, string.Empty, string.Empty),
                    Providers: new ProvidersView(
                        AcoustId: effective.EnableAcoustIdProvider,
                        MusicBrainzWeb: effective.EnableMusicBrainzWebProvider,
                        SpotifyApi: effective.EnableSpotifyApiProvider,
                        Tracker: effective.EnableTrackerProvider,
                        Deezer: effective.EnableDeezerProvider,
                        AppleMusic: effective.EnableAppleMusicProvider),
                    Spotify: new SpotifyView(
                        OAuthRedirectBaseUrl: spotifyOptions.Value.OAuthRedirectBaseUrl,
                        Scopes: SpotifyScopes),
                    QualityGrading: new QualityGradingView(
                        Enabled: effective.QualityGradingEnabled,
                        Configured: qualityConfigured),
                    // Experimental AI lyrics transcription: only "enabled" when a transcription provider
                    // (key + base URL) is configured on the server, so the UI can hide it entirely otherwise.
                    LyricsTranscription: new LyricsTranscriptionView(
                        Enabled: lyricsTranscriptionOptions.Value.IsConfigured),
                    // Wishlist downloads: Enabled is the deploy-time feature switch (yt-dlp + a writable
                    // download dir); AutoDownload is the runtime toggle the owner flips from the UI. The
                    // UI shows the toggle only when the feature is enabled.
                    Downloads: new DownloadsView(
                        Enabled: opts.EnableWishlistDownloads,
                        AutoDownload: effective.AutoDownloadWishlist),
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

                var update = new RuntimeSettingsUpdate
                {
                    EnableAcoustIdProvider = request.Providers?.AcoustId,
                    EnableMusicBrainzWebProvider = request.Providers?.MusicBrainzWeb,
                    EnableSpotifyApiProvider = request.Providers?.SpotifyApi,
                    EnableTrackerProvider = request.Providers?.Tracker,
                    EnableDeezerProvider = request.Providers?.Deezer,
                    EnableAppleMusicProvider = request.Providers?.AppleMusic,
                    QualityGradingEnabled = request.QualityGrading?.Enabled,
                    AutoDownloadWishlist = request.Downloads?.AutoDownload,
                };

                var effective = await runtimeSettings.UpdateAsync(update, ct);
                return Results.Ok(new
                {
                    providers = new ProvidersView(
                        effective.EnableAcoustIdProvider,
                        effective.EnableMusicBrainzWebProvider,
                        effective.EnableSpotifyApiProvider,
                        effective.EnableTrackerProvider,
                        effective.EnableDeezerProvider,
                        effective.EnableAppleMusicProvider),
                    qualityGrading = new { enabled = effective.QualityGradingEnabled },
                    downloads = new { autoDownload = effective.AutoDownloadWishlist },
                    updatedAtUtc = effective.UpdatedAtUtc,
                });
            })
            .WithName("UpdateSettings")
            .WithSummary("Updates the persisted runtime settings overlay (provider toggles + quality grading). Takes effect on the next enrichment cycle.")
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
    SpotifyView Spotify,
    QualityGradingView QualityGrading,
    LyricsTranscriptionView LyricsTranscription,
    DownloadsView Downloads,
    DateTime? UpdatedAtUtc);

public sealed record PathsView(string SourceDirectory, string DestinationDirectory, string FpcalcPath);
public sealed record ProvidersView(bool AcoustId, bool MusicBrainzWeb, bool SpotifyApi, bool Tracker, bool Deezer, bool AppleMusic);
public sealed record SpotifyView(string OAuthRedirectBaseUrl, IReadOnlyList<string> Scopes);
public sealed record QualityGradingView(bool Enabled, bool Configured);
public sealed record LyricsTranscriptionView(bool Enabled);
public sealed record DownloadsView(bool Enabled, bool AutoDownload);

public sealed record SettingsUpdateRequest(ProvidersUpdate? Providers, QualityGradingUpdate? QualityGrading, DownloadsUpdate? Downloads);
public sealed record QualityGradingUpdate(bool? Enabled);
public sealed record DownloadsUpdate(bool? AutoDownload);
public sealed record ProvidersUpdate(bool? AcoustId, bool? MusicBrainzWeb, bool? SpotifyApi, bool? Tracker, bool? Deezer, bool? AppleMusic);
