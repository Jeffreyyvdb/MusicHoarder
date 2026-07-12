using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Wishlist;

namespace MusicHoarder.Api.Endpoints;

public static class WishlistEndpoints
{
    public static IEndpointRouteBuilder MapWishlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wishlist").WithTags("Wishlist").RequireOwner();

        group.MapGet("/", async (
                string? status,
                int? offset,
                int? limit,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                WishlistItemStatus? statusFilter = null;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    if (!Enum.TryParse(status, ignoreCase: true, out WishlistItemStatus parsed))
                        return Results.BadRequest(new { message = $"Invalid status '{status}'." });
                    statusFilter = parsed;
                }

                var take = Math.Clamp(limit ?? 50, 1, 200);
                var skip = Math.Max(offset ?? 0, 0);

                var query = db.WishlistItems.AsNoTracking().AsQueryable();
                if (statusFilter is { } s)
                    query = query.Where(w => w.Status == s);

                var total = await query.CountAsync(ct);
                var items = await query
                    .OrderByDescending(w => w.SpotifyAddedAtUtc)
                    .ThenByDescending(w => w.Id)
                    .Skip(skip)
                    .Take(take)
                    .Select(w => new WishlistItemDto(
                        w.Id,
                        w.SpotifyTrackId,
                        w.DeezerTrackId,
                        w.Title,
                        w.Artist,
                        w.Album,
                        w.Isrc,
                        w.DurationMs,
                        w.AlbumArt,
                        w.SpotifyAddedAtUtc,
                        w.Status.ToString(),
                        w.DownloadProvider,
                        w.DownloadedFilePath,
                        w.DownloadedSongId,
                        w.AttemptCount,
                        w.LastError,
                        w.DownloadedSong != null ? w.DownloadedSong.EnrichmentStatus.ToString() : null,
                        w.DownloadedSong != null ? w.DownloadedSong.LibraryBuildStatus.ToString() : null,
                        w.CreatedAtUtc,
                        w.UpdatedAtUtc))
                    .ToListAsync(ct);

                return Results.Ok(new WishlistItemsResponse(total, skip, take, items));
            })
            .WithName("GetWishlist")
            .WithSummary("Paginated wishlist items, optionally filtered by status.");

        group.MapGet("/sources", async (MusicHoarderDbContext db, CancellationToken ct) =>
            {
                var sources = await db.WishlistSources
                    .AsNoTracking()
                    .OrderBy(s => s.SourceType)
                    .ThenBy(s => s.Name)
                    .Select(s => new WishlistSourceDto(
                        s.Id,
                        s.SourceType.ToString(),
                        s.SourceType == WishlistSourceType.DeezerPlaylist ? "deezer" : "spotify",
                        s.SpotifyPlaylistId,
                        s.DeezerPlaylistId,
                        s.Name,
                        s.ImageUrl,
                        s.AutoSync,
                        s.LastSyncedAtUtc,
                        s.CreatedAtUtc,
                        db.WishlistItems.Count(w => w.WishlistSourceId == s.Id)))
                    .ToListAsync(ct);

                return Results.Ok(new { sources });
            })
            .WithName("GetWishlistSources")
            .WithSummary("Lists the owner's wishlist sources.");

        group.MapPost("/sources", async (
                AddWishlistSourceRequest body,
                IWishlistService wishlist,
                IServiceScopeFactory scopeFactory,
                ICurrentUserAccessor currentUser,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                // Accept both the Spotify shape ({ type, playlistId }) and the Deezer discover shape
                // ({ sourceType, deezerPlaylistId }); the front-ends differ by provider.
                var typeStr = body.SourceType ?? body.Type;
                if (!Enum.TryParse(typeStr, ignoreCase: true, out WishlistSourceType type))
                    return Results.BadRequest(new { message = "type must be 'LikedSongs', 'Playlist' or 'DeezerPlaylist'." });

                var playlistId = type == WishlistSourceType.DeezerPlaylist ? body.DeezerPlaylistId : body.PlaylistId;

                try
                {
                    // Create the source row synchronously (fast), then snapshot its tracks off the
                    // request path: a large library is dozens of sequential provider calls and would
                    // blow past the gateway timeout (504). The snapshot persists per page, so items
                    // appear on the wishlist progressively; auto-synced sources also self-heal on the
                    // periodic WishlistSyncBackgroundService tick if this run is interrupted.
                    var ownerId = currentUser.UserId;
                    var source = await wishlist.CreateOrUpdateSourceAsync(ownerId, type, playlistId, body.AutoSync ?? false, ct);

                    var sourceId = source.Id;
                    _ = Task.Run(async () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        var logger = loggerFactory.CreateLogger("WishlistSourceSnapshot");
                        try
                        {
                            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
                            var svc = scope.ServiceProvider.GetRequiredService<IWishlistService>();
                            var src = await db.WishlistSources
                                .IgnoreQueryFilters()
                                .FirstOrDefaultAsync(s => s.Id == sourceId, CancellationToken.None);
                            if (src is not null)
                            {
                                var result = await svc.SyncSourceAsync(ownerId, src, CancellationToken.None);

                                // Wake the download worker now rather than waiting its idle poll, so a
                                // just-added source's tracks start fetching immediately. Gated by the
                                // feature switch (config) + the auto-download runtime setting (mirrors the
                                // worker's own auto-sweep gate).
                                var opts = scope.ServiceProvider.GetRequiredService<IOptions<MusicEnricherOptions>>().Value;
                                var runtime = scope.ServiceProvider.GetRequiredService<Settings.IRuntimeSettingsService>();
                                var autoDownload = (await runtime.GetAsync(CancellationToken.None)).AutoDownloadWishlist;
                                if (result.Added > 0 && opts.EnableWishlistDownloads && autoDownload)
                                {
                                    var jobManager = scope.ServiceProvider.GetRequiredService<JobManager>();
                                    jobManager.TryStartJob(JobType.Download, out _, out _);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Background wishlist snapshot failed for source {SourceId} (auto-sync will retry)", sourceId);
                        }
                    });

                    return Results.Json(new { sourceId, queued = true }, statusCode: StatusCodes.Status202Accepted);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
                catch (SpotifyNotConnectedException)
                {
                    return Results.Json(new { error = "spotify_not_connected" }, statusCode: 401);
                }
                catch (SpotifyRateLimitException)
                {
                    return Results.Json(new { error = "rate_limit_exceeded" }, statusCode: 429);
                }
            })
            .WithName("AddWishlistSource")
            .WithSummary("Add Liked Songs, a Spotify playlist, or a Deezer discover playlist as a wishlist source; tracks snapshot in the background.");

        group.MapPatch("/sources/{id:int}", async (
                int id,
                PatchWishlistSourceRequest body,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                var source = await db.WishlistSources.FirstOrDefaultAsync(s => s.Id == id, ct);
                if (source is null)
                    return Results.NotFound(new { message = $"Wishlist source {id} not found." });

                if (body.AutoSync is { } autoSync)
                    source.AutoSync = autoSync;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { source.Id, source.AutoSync });
            })
            .WithName("UpdateWishlistSource")
            .WithSummary("Toggle a wishlist source's auto-sync.");

        group.MapDelete("/sources/{id:int}", async (int id, MusicHoarderDbContext db, CancellationToken ct) =>
            {
                var source = await db.WishlistSources.FirstOrDefaultAsync(s => s.Id == id, ct);
                if (source is null)
                    return Results.NotFound(new { message = $"Wishlist source {id} not found." });

                // Items survive (their FK nulls out via OnDelete.SetNull) so already-acquired tracks remain.
                db.WishlistSources.Remove(source);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { message = "Source removed." });
            })
            .WithName("DeleteWishlistSource")
            .WithSummary("Remove a wishlist source (keeps already-acquired items).");

        group.MapPost("/items/{id:int}/retry", async (int id, MusicHoarderDbContext db, CancellationToken ct) =>
            {
                var item = await db.WishlistItems.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (item is null)
                    return Results.NotFound(new { message = $"Wishlist item {id} not found." });

                item.Status = WishlistItemStatus.Pending;
                item.LastError = null;
                item.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { item.Id, status = item.Status.ToString() });
            })
            .WithName("RetryWishlistItem")
            .WithSummary("Reset a wishlist item to Pending so the download worker retries it.");

        group.MapPost("/items/retry-failed", async (MusicHoarderDbContext db, CancellationToken ct) =>
            {
                // Bulk-requeue every Failed/NotFound item to Pending so the next download sweep
                // re-attempts them — used after fixing a systemic cause (e.g. a missing downloader
                // dependency) where retrying thousands of rows one by one is impractical.
                var now = DateTime.UtcNow;
                var reset = await db.WishlistItems
                    .Where(w => w.Status == WishlistItemStatus.Failed
                        || w.Status == WishlistItemStatus.NotFound
                        || w.Status == WishlistItemStatus.Downloading)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.Status, WishlistItemStatus.Pending)
                        .SetProperty(w => w.LastError, (string?)null)
                        .SetProperty(w => w.UpdatedAtUtc, now), ct);

                return Results.Ok(new { reset });
            })
            .WithName("RetryFailedWishlistItems")
            .WithSummary("Reset all Failed/NotFound items to Pending so the next download sweep retries them.");

        group.MapDelete("/items/{id:int}", async (int id, MusicHoarderDbContext db, CancellationToken ct) =>
            {
                var item = await db.WishlistItems.FirstOrDefaultAsync(w => w.Id == id, ct);
                if (item is null)
                    return Results.NotFound(new { message = $"Wishlist item {id} not found." });

                db.WishlistItems.Remove(item);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { message = "Item removed." });
            })
            .WithName("DeleteWishlistItem")
            .WithSummary("Remove a wishlist item.");

        group.MapPost("/download", (JobManager jobManager, IOptions<MusicEnricherOptions> options) =>
            {
                var opts = options.Value;
                if (!opts.EnableWishlistDownloads)
                    return Results.Conflict(new { message = "Wishlist downloads are disabled (MusicEnricher:EnableWishlistDownloads)." });
                if (string.IsNullOrWhiteSpace(opts.DownloadDirectory))
                    return Results.Conflict(new { message = "No download directory is configured (MusicEnricher:DownloadDirectory)." });

                if (!jobManager.TryStartJob(JobType.Download, out var jobId, out _))
                    return Results.Conflict(new { message = "A download job is already running." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerWishlistDownload")
            .WithSummary("Kick off a download sweep of Pending wishlist items.");

        return app;
    }
}

public sealed record WishlistItemDto(
    int Id,
    string? SpotifyTrackId,
    string? DeezerTrackId,
    string Title,
    string Artist,
    string? Album,
    string? Isrc,
    int DurationMs,
    string? AlbumArt,
    DateTime? SpotifyAddedAtUtc,
    string Status,
    string? DownloadProvider,
    string? DownloadedFilePath,
    int? DownloadedSongId,
    int AttemptCount,
    string? LastError,
    string? LibraryEnrichmentStatus,
    string? LibraryBuildStatus,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record WishlistItemsResponse(
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<WishlistItemDto> Items);

public sealed record WishlistSourceDto(
    int Id,
    string SourceType,
    string Provider,
    string? SpotifyPlaylistId,
    string? DeezerPlaylistId,
    string Name,
    string? ImageUrl,
    bool AutoSync,
    DateTime? LastSyncedAtUtc,
    DateTime CreatedAtUtc,
    int ItemCount);

public sealed record AddWishlistSourceRequest(
    string? Type,
    string? SourceType,
    string? PlaylistId,
    string? DeezerPlaylistId,
    bool? AutoSync);

public sealed record PatchWishlistSourceRequest(bool? AutoSync);
