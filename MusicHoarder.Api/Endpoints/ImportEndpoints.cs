using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Discover;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Enrichment;
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
        var group = app.MapGroup("/api/import").WithTags("Import").RequireAdmin();

        group.MapPost("/resolve", async (
                ImportResolveRequest body,
                ISpotifyCatalogSearchService catalog,
                IYouTubeMetadataResolver youtube,
                MusicHoarderDbContext db,
                IOptions<SpotifyOptions> spotifyOptions,
                CancellationToken ct) =>
            {
                if (!ImportUrlParser.TryParse(body.Url, out var kind, out var id))
                {
                    // A playlist is not one track, but it is something this app can add: say so, with
                    // the provider, so the dialog can offer to keep the playlist in sync instead.
                    if (PlaylistUrlParser.TryParse(body.Url, out var playlistProvider, out var playlistId))
                        return Results.BadRequest(new
                        {
                            error = "playlist_link",
                            message = "That is a playlist link, not a single track.",
                            provider = playlistProvider,
                            playlistId,
                        });

                    return Results.BadRequest(new
                    {
                        error = "invalid_url",
                        message = "Paste a Spotify track link (open.spotify.com/track/…) or a YouTube video link.",
                    });
                }

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
                if (!probe.Ok)
                {
                    // Lead with the actionable hint (bot check / missing JS runtime / private / …) and
                    // fall back to a generic message; include the raw yt-dlp detail for diagnostics.
                    var message = probe.Hint
                        ?? "Could not read that YouTube video. It may be private, age-restricted, or yt-dlp is unavailable.";
                    return Results.UnprocessableEntity(new
                    {
                        error = probe.BinaryMissing ? "youtube_unavailable" : "youtube_probe_failed",
                        message,
                        detail = probe.Detail,
                    });
                }

                var result = probe.Result!;
                return Results.Ok(new ImportResolveResponse(
                    Source: "youtube",
                    Title: result.Title,
                    Artist: result.Artist,
                    // YouTube Music entries carry a real album; a plain video has none, so the preview
                    // offers the single name the import would file it under (editable by the owner).
                    Album: DownloadTagWriter.ResolveAlbum(result.Album, result.Title),
                    DurationMs: result.DurationMs,
                    CoverUrl: result.ThumbnailUrl,
                    SpotifyTrackId: null,
                    Isrc: null,
                    SourceUrl: watchUrl));
            })
            .WithName("ImportResolve")
            .WithSummary("Resolve a pasted Spotify track / YouTube video URL into editable, download-ready metadata.");

        group.MapPost("/", ImportTrack)
            .WithName("ImportTrack")
            .WithSummary("Queue a resolved track for download (creates a wishlist item and triggers a download sweep).");

        return app;
    }

    internal static async Task<IResult> ImportTrack(
        ImportTrackRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        JobManager jobManager,
        IDeezerCatalogService deezer,
        IOptions<MusicEnricherOptions> options,
        CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.EnableWishlistDownloads)
            return Results.Conflict(new { error = "downloads_disabled", message = "Downloads are disabled (MusicEnricher:EnableWishlistDownloads)." });
        if (string.IsNullOrWhiteSpace(opts.DownloadDirectory))
            return Results.Conflict(new { error = "no_download_directory", message = "No download directory is configured (MusicEnricher:DownloadDirectory)." });

        // A Deezer track fills whatever the body left blank, so a client can send the id alone.
        var deezerTrackId = string.IsNullOrWhiteSpace(body.DeezerTrackId) ? null : body.DeezerTrackId.Trim();
        DeezerCatalogTrack? deezerTrack = null;
        if (deezerTrackId is not null)
        {
            if (!CatalogIds.IsValid(deezerTrackId))
                return Results.BadRequest(new { error = "invalid_deezer_id", message = "A Deezer track id is digits only." });

            try
            {
                deezerTrack = await deezer.LookupByIdAsync(deezerTrackId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or ProviderRateLimitedException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Json(
                    new { error = "catalog_unavailable", message = "Deezer could not be reached. Try again in a moment." },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            if (deezerTrack is null)
                return Results.NotFound(new { error = "not_found", message = "Deezer has no such track." });
        }

        var title = FirstFilled(body.Title, deezerTrack?.Title);
        var artist = FirstFilled(body.Artist, deezerTrack?.Artist);
        if (title.Length == 0)
            return Results.BadRequest(new { error = "title_required", message = "A title is required." });

        // Never queue an albumless item — the downloader stamps this onto the file, and a blank
        // album is what lands the track in an "Unknown Album" destination folder with no cover.
        var album = DownloadTagWriter.ResolveAlbum(FirstFilled(body.Album, deezerTrack?.AlbumName), title);
        var isrc = NullIfBlank(FirstFilled(body.Isrc, deezerTrack?.Isrc));
        var coverUrl = NullIfBlank(FirstFilled(body.CoverUrl, deezerTrack?.CoverUrl));
        var durationMs = body.DurationMs is > 0 ? body.DurationMs.Value : deezerTrack?.DurationMs ?? 0;

        // Re-validate a supplied SourceUrl through the parser rather than trusting the client —
        // yt-dlp downloads exactly what it's handed, so only accept a recognized YouTube URL and
        // re-canonicalize it. Spotify imports carry a track id and no SourceUrl.
        string? sourceUrl = null;
        string? youTubeVideoId = null;
        var spotifyTrackId = string.IsNullOrWhiteSpace(body.SpotifyTrackId) ? null : body.SpotifyTrackId!.Trim();
        if (!string.IsNullOrWhiteSpace(body.SourceUrl))
        {
            if (!ImportUrlParser.TryParse(body.SourceUrl, out var kind, out var vid) || kind != ImportUrlKind.YouTube)
                return Results.BadRequest(new { error = "invalid_url", message = "Source URL must be a valid YouTube link." });
            sourceUrl = ImportUrlParser.YouTubeWatchUrl(vid);
            youTubeVideoId = vid;
        }

        if (spotifyTrackId is null && sourceUrl is null && deezerTrackId is null)
            return Results.BadRequest(new { error = "no_source", message = "Provide a Spotify track id, a Deezer track id or a YouTube source URL." });

        var ownerId = currentUser.UserId;
        var now = DateTime.UtcNow;

        // The ISRC only identifies a track here when Deezer vouched for it; a client-typed one is not a key.
        var dedupeIsrc = NullIfBlank(deezerTrack?.Isrc);

        // Idempotent: re-importing the same track requeues the existing row instead of piling up
        // duplicates (matches the wishlist's per-track dedupe on sync) — including a video a
        // synced YouTube playlist already put on the wishlist.
        // The Deezer id is unique per owner, so the row holding it wins: requeueing another match
        // and giving it this id would collide with that row. An ISRC is only a fallback key.
        var candidates = await db.WishlistItems.Where(w =>
            (sourceUrl != null && w.SourceUrl == sourceUrl) ||
            (youTubeVideoId != null && w.YouTubeVideoId == youTubeVideoId) ||
            (spotifyTrackId != null && w.SpotifyTrackId == spotifyTrackId) ||
            (deezerTrackId != null && w.DeezerTrackId == deezerTrackId) ||
            (dedupeIsrc != null && w.Isrc == dedupeIsrc)).ToListAsync(ct);
        var existing =
            candidates.FirstOrDefault(w => deezerTrackId != null && w.DeezerTrackId == deezerTrackId)
            ?? candidates.FirstOrDefault(w =>
                (sourceUrl != null && w.SourceUrl == sourceUrl) ||
                (youTubeVideoId != null && w.YouTubeVideoId == youTubeVideoId) ||
                (spotifyTrackId != null && w.SpotifyTrackId == spotifyTrackId))
            ?? candidates.FirstOrDefault();

        // A row found only through the ISRC is another import of the same recording (a Spotify like,
        // a playlist). Requeueing it would restart a download in progress or fetch a finished one
        // again, so it is only taken over when its download came to nothing.
        var matchedByIsrcOnly = existing is not null
            && !(deezerTrackId != null && existing.DeezerTrackId == deezerTrackId)
            && !(sourceUrl != null && existing.SourceUrl == sourceUrl)
            && !(youTubeVideoId != null && existing.YouTubeVideoId == youTubeVideoId)
            && !(spotifyTrackId != null && existing.SpotifyTrackId == spotifyTrackId);
        if (existing is not null && matchedByIsrcOnly
            && existing.Status is not (WishlistItemStatus.Failed or WishlistItemStatus.NotFound))
        {
            existing.DeezerTrackId ??= deezerTrackId;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { wishlistItemId = existing.Id, jobStarted = false, jobId = (Guid?)null, status = existing.Status.ToString() });
        }

        WishlistItem item;
        if (existing is not null)
        {
            existing.Requeue(now);
            existing.Title = title;
            existing.Artist = artist;
            existing.Album = album;
            existing.Isrc = isrc ?? existing.Isrc;
            existing.DeezerTrackId ??= deezerTrackId;
            if (durationMs > 0) existing.DurationMs = durationMs;
            if (coverUrl is not null) existing.AlbumArt = coverUrl;
            if (body.DownloadMusicVideo is not null) existing.DownloadMusicVideo = body.DownloadMusicVideo;
            // A row imported before the column existed: it is this video, so give it the key.
            if (existing.SourceUrl == sourceUrl) existing.YouTubeVideoId ??= youTubeVideoId;
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
                DeezerTrackId = deezerTrackId,
                SourceUrl = sourceUrl,
                YouTubeVideoId = youTubeVideoId,
                Title = title,
                Artist = artist,
                Album = album,
                Isrc = isrc,
                DurationMs = durationMs,
                AlbumArt = coverUrl,
                DownloadMusicVideo = body.DownloadMusicVideo,
                // Manually imported "right now" — sort it to the top of the wishlist (which orders
                // by SpotifyAddedAtUtc desc) and prioritize it in the download batch.
                SpotifyAddedAtUtc = now,
                Status = WishlistItemStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.WishlistItems.Add(item);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (deezerTrackId is not null && existing is null)
        {
            // Another import of this Deezer track saved first (the id is unique per owner): that row
            // is the answer.
            db.ChangeTracker.Clear();
            var winner = await db.WishlistItems.AsNoTracking().FirstOrDefaultAsync(w => w.DeezerTrackId == deezerTrackId, ct);
            if (winner is null)
                throw;
            return Results.Json(
                new { wishlistItemId = winner.Id, jobStarted = false, jobId = (Guid?)null, status = winner.Status.ToString() },
                statusCode: StatusCodes.Status202Accepted);
        }

        // Kick a download sweep now. If another job is running, TryStartJob fails and the item
        // stays Pending — it's picked up when the current job finishes (auto-sweep) or on the
        // next manual "Download" trigger.
        var jobStarted = jobManager.TryStartJob(JobType.Download, out var jobId, out _);

        return Results.Json(
            new { wishlistItemId = item.Id, jobStarted, jobId = jobStarted ? jobId : (Guid?)null, status = item.Status.ToString() },
            statusCode: StatusCodes.Status202Accepted);
    }

    private static string FirstFilled(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred.Trim() : fallback?.Trim() ?? "";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
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
    string? SourceUrl,
    // "Also download the music video" — null follows MusicEnricher:DownloadMusicVideos.
    bool? DownloadMusicVideo = null,
    // A catalog pick: the server fills what the rest of the body leaves blank from Deezer.
    string? DeezerTrackId = null);
