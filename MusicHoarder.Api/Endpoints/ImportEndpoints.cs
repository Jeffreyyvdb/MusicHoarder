using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Single-track "add from URL": paste a Spotify track or YouTube video link, resolve it to editable
/// metadata, then queue it for download. The download flows through the existing
/// scan → fingerprint → enrich → build pipeline like any wishlist item — and on a Push instance, the
/// built track auto-syncs to the public receiver. Owner-only.
/// </summary>
public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/import").WithTags("Import").RequireOwner();

        group.MapPost("/resolve", async (
                ImportResolveRequest body,
                ISpotifyCatalogSearchService catalog,
                IYouTubeMetadataResolver youtube,
                MusicHoarderDbContext db,
                IOptions<SpotifyOptions> spotifyOptions,
                CancellationToken ct) =>
            {
                if (!ImportUrlParser.TryParse(body.Url, out var kind, out var id))
                    return Results.BadRequest(new
                    {
                        error = "invalid_url",
                        message = "Paste a Spotify track link (open.spotify.com/track/…) or a YouTube video link.",
                    });

                if (kind == ImportUrlKind.SpotifyTrack)
                {
                    var settings = await db.SpotifySettings.AsNoTracking().FirstOrDefaultAsync(ct);
                    var (clientId, clientSecret) = SpotifyAppCredentialsResolver.Resolve(settings, spotifyOptions.Value);
                    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                        return Results.UnprocessableEntity(new
                        {
                            error = "spotify_not_configured",
                            message = "Spotify API credentials are not configured. Add them in Settings to import Spotify links.",
                        });

                    var track = await catalog.GetTrackAsync(clientId!, clientSecret!, id, ct);
                    if (track is null)
                        return Results.NotFound(new
                        {
                            error = "not_found",
                            message = "That Spotify track could not be found.",
                        });

                    return Results.Ok(new ImportResolveResponse(
                        Source: "spotify",
                        Title: track.Title,
                        Artist: track.Artist,
                        Album: string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName,
                        DurationMs: track.DurationMs,
                        CoverUrl: track.AlbumArtUrl,
                        SpotifyTrackId: track.Id,
                        Isrc: track.Isrc,
                        SourceUrl: null));
                }

                // YouTube: probe the canonical watch URL (tracking/playlist params already stripped).
                var watchUrl = ImportUrlParser.YouTubeWatchUrl(id);
                var probe = await youtube.ProbeAsync(watchUrl, ct);
                if (probe is null)
                    return Results.UnprocessableEntity(new
                    {
                        error = "youtube_probe_failed",
                        message = "Could not read that YouTube video. It may be private, age-restricted, or yt-dlp is unavailable.",
                    });

                return Results.Ok(new ImportResolveResponse(
                    Source: "youtube",
                    Title: probe.Title,
                    Artist: probe.Artist,
                    Album: null,
                    DurationMs: probe.DurationMs,
                    CoverUrl: probe.ThumbnailUrl,
                    SpotifyTrackId: null,
                    Isrc: null,
                    SourceUrl: watchUrl));
            })
            .WithName("ImportResolve")
            .WithSummary("Resolve a pasted Spotify track / YouTube video URL into editable, download-ready metadata.");

        group.MapPost("/", async (
                ImportTrackRequest body,
                MusicHoarderDbContext db,
                ICurrentUserAccessor currentUser,
                JobManager jobManager,
                IOptions<MusicEnricherOptions> options,
                CancellationToken ct) =>
            {
                var opts = options.Value;
                if (!opts.EnableWishlistDownloads)
                    return Results.Conflict(new { error = "downloads_disabled", message = "Downloads are disabled (MusicEnricher:EnableWishlistDownloads)." });
                if (string.IsNullOrWhiteSpace(opts.DownloadDirectory))
                    return Results.Conflict(new { error = "no_download_directory", message = "No download directory is configured (MusicEnricher:DownloadDirectory)." });

                var title = body.Title?.Trim() ?? "";
                var artist = body.Artist?.Trim() ?? "";
                if (title.Length == 0)
                    return Results.BadRequest(new { error = "title_required", message = "A title is required." });

                // Re-validate a supplied SourceUrl through the parser rather than trusting the client —
                // yt-dlp downloads exactly what it's handed, so only accept a recognized YouTube URL and
                // re-canonicalize it. Spotify imports carry a track id and no SourceUrl.
                string? sourceUrl = null;
                var spotifyTrackId = string.IsNullOrWhiteSpace(body.SpotifyTrackId) ? null : body.SpotifyTrackId!.Trim();
                if (!string.IsNullOrWhiteSpace(body.SourceUrl))
                {
                    if (!ImportUrlParser.TryParse(body.SourceUrl, out var kind, out var vid) || kind != ImportUrlKind.YouTube)
                        return Results.BadRequest(new { error = "invalid_url", message = "Source URL must be a valid YouTube link." });
                    sourceUrl = ImportUrlParser.YouTubeWatchUrl(vid);
                }

                if (spotifyTrackId is null && sourceUrl is null)
                    return Results.BadRequest(new { error = "no_source", message = "Provide a Spotify track id or a YouTube source URL." });

                var ownerId = currentUser.UserId;
                var now = DateTime.UtcNow;

                // Idempotent: re-importing the same track requeues the existing row instead of piling up
                // duplicates (matches the wishlist's per-track dedupe on sync).
                var existing = await db.WishlistItems.FirstOrDefaultAsync(w =>
                    (sourceUrl != null && w.SourceUrl == sourceUrl) ||
                    (spotifyTrackId != null && w.SpotifyTrackId == spotifyTrackId), ct);

                WishlistItem item;
                if (existing is not null)
                {
                    existing.Status = WishlistItemStatus.Pending;
                    existing.LastError = null;
                    existing.Title = title;
                    existing.Artist = artist;
                    existing.Album = string.IsNullOrWhiteSpace(body.Album) ? existing.Album : body.Album!.Trim();
                    existing.Isrc = string.IsNullOrWhiteSpace(body.Isrc) ? existing.Isrc : body.Isrc!.Trim();
                    if (body.DurationMs is > 0) existing.DurationMs = body.DurationMs.Value;
                    if (!string.IsNullOrWhiteSpace(body.CoverUrl)) existing.AlbumArt = body.CoverUrl!.Trim();
                    existing.UpdatedAtUtc = now;
                    item = existing;
                }
                else
                {
                    item = new WishlistItem
                    {
                        OwnerUserId = ownerId,
                        WishlistSourceId = null,
                        SpotifyTrackId = spotifyTrackId,
                        SourceUrl = sourceUrl,
                        Title = title,
                        Artist = artist,
                        Album = string.IsNullOrWhiteSpace(body.Album) ? null : body.Album!.Trim(),
                        Isrc = string.IsNullOrWhiteSpace(body.Isrc) ? null : body.Isrc!.Trim(),
                        DurationMs = body.DurationMs is > 0 ? body.DurationMs.Value : 0,
                        AlbumArt = string.IsNullOrWhiteSpace(body.CoverUrl) ? null : body.CoverUrl!.Trim(),
                        // Manually imported "right now" — sort it to the top of the wishlist (which orders
                        // by SpotifyAddedAtUtc desc) and prioritize it in the download batch.
                        SpotifyAddedAtUtc = now,
                        Status = WishlistItemStatus.Pending,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    db.WishlistItems.Add(item);
                }

                await db.SaveChangesAsync(ct);

                // Kick a download sweep now. If another job is running, TryStartJob fails and the item
                // stays Pending — it's picked up when the current job finishes (auto-sweep) or on the
                // next manual "Download" trigger.
                var jobStarted = jobManager.TryStartJob(JobType.Download, out var jobId, out _);

                return Results.Json(
                    new { wishlistItemId = item.Id, jobStarted, jobId = jobStarted ? jobId : (Guid?)null },
                    statusCode: StatusCodes.Status202Accepted);
            })
            .WithName("ImportTrack")
            .WithSummary("Queue a resolved track for download (creates a wishlist item and triggers a download sweep).");

        return app;
    }
}

public sealed record ImportResolveRequest(string? Url);

public sealed record ImportResolveResponse(
    string Source,
    string Title,
    string Artist,
    string? Album,
    int DurationMs,
    string? CoverUrl,
    string? SpotifyTrackId,
    string? Isrc,
    string? SourceUrl);

public sealed record ImportTrackRequest(
    string? Source,
    string? Title,
    string? Artist,
    string? Album,
    int? DurationMs,
    string? CoverUrl,
    string? SpotifyTrackId,
    string? Isrc,
    string? SourceUrl);
