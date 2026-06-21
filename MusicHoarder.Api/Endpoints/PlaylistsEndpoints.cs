using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Endpoints;

public static class PlaylistsEndpoints
{
    public static IEndpointRouteBuilder MapPlaylistsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/playlists").WithTags("Playlists").RequireOwner();

        group.MapGet("/", async (
                ISpotifyApiService spotifyApi,
                MusicHoarderDbContext db,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                // Subscriptions = the ExportedPlaylist rows (opt-in). Always available even if Spotify is
                // unreachable, so the owner can still unsubscribe.
                var rows = await db.ExportedPlaylists.AsNoTracking().ToListAsync(ct);
                var subs = rows.ToDictionary(r => CollectionKey(r.Kind, r.SpotifyPlaylistId), StringComparer.Ordinal);

                var collections = new List<PlaylistCollectionDto>();
                var connected = true;
                string? spotifyError = null;

                try
                {
                    // Liked Songs total is cheap (single-item page just for the count).
                    var liked = await spotifyApi.GetLikedSongsAsync(0, 1, ct);
                    subs.TryGetValue(CollectionKey(ExportedPlaylistKind.LikedSongs, null), out var likedRow);
                    collections.Add(ToDto(ExportedPlaylistKind.LikedSongs, null, "Liked Songs", null, null, liked.Total, likedRow));

                    var playlists = await spotifyApi.GetPlaylistsAsync(ct);
                    foreach (var p in playlists.Items.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        subs.TryGetValue(CollectionKey(ExportedPlaylistKind.Playlist, p.SpotifyId), out var row);
                        collections.Add(ToDto(ExportedPlaylistKind.Playlist, p.SpotifyId, p.Name, p.ImageUrl, p.OwnerName, p.TrackCount, row));
                    }
                }
                catch (SpotifyNotConnectedException)
                {
                    connected = false;
                    collections = SubscribedOnly(rows);
                }
                catch (Exception ex)
                {
                    // Rate limit / transient Spotify failure: still show what's subscribed.
                    spotifyError = "Couldn't load your full Spotify library right now. Showing synced playlists only.";
                    loggerFactory.CreateLogger("Playlists")
                        .LogWarning(ex, "Failed to list Spotify collections; degrading to subscribed-only");
                    collections = SubscribedOnly(rows);
                }

                return Results.Ok(new PlaylistCollectionsResponse(connected, spotifyError, collections));
            })
            .WithName("GetPlaylistCollections")
            .WithSummary("Lists the owner's Spotify collections (Liked Songs + playlists) with subscription state and M3U coverage.");

        group.MapPost("/subscribe", async (
                SubscribePlaylistRequest body,
                IPlaylistExportService export,
                IServiceScopeFactory scopeFactory,
                IOptions<MusicEnricherOptions> options,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                if (!Enum.TryParse(body.Kind, ignoreCase: true, out ExportedPlaylistKind kind))
                    return Results.BadRequest(new { message = "kind must be 'LikedSongs' or 'Playlist'." });

                ExportedPlaylist row;
                try
                {
                    row = await export.SubscribeAsync(kind, body.SpotifyPlaylistId, body.Name ?? string.Empty, ct);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }

                // Write this subscription's .m3u8 off the request path: a full export is dozens of
                // sequential Spotify calls and would blow past the gateway timeout. The export
                // self-serializes, so it safely folds into any in-flight run.
                if (options.Value.EnablePlaylistExport)
                {
                    _ = Task.Run(async () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        var logger = loggerFactory.CreateLogger("PlaylistSubscribeTrigger");
                        try
                        {
                            var svc = scope.ServiceProvider.GetRequiredService<IPlaylistExportService>();
                            await svc.RunExportAsync(CancellationToken.None);
                        }
                        catch (SpotifyNotConnectedException)
                        {
                            logger.LogInformation("Subscribe export skipped: Spotify not connected");
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Subscribe-triggered playlist export failed");
                        }
                    });
                }

                return Results.Json(new { id = row.Id, subscribed = true, queued = true }, statusCode: StatusCodes.Status202Accepted);
            })
            .WithName("SubscribePlaylist")
            .WithSummary("Subscribe a Spotify collection (Liked Songs or a playlist) so it is mirrored to an on-disk M3U.");

        group.MapDelete("/{id:int}", async (int id, IPlaylistExportService export, CancellationToken ct) =>
            {
                var removed = await export.UnsubscribeAsync(id, ct);
                return removed
                    ? Results.Ok(new { message = "Unsubscribed." })
                    : Results.NotFound(new { message = $"Playlist subscription {id} not found." });
            })
            .WithName("UnsubscribePlaylist")
            .WithSummary("Unsubscribe a collection: deletes its .m3u8 file and stops mirroring it.");

        group.MapPost("/regenerate", (
                IServiceScopeFactory scopeFactory,
                IOptions<MusicEnricherOptions> options,
                ILoggerFactory loggerFactory) =>
            {
                if (!options.Value.EnablePlaylistExport)
                    return Results.Conflict(new { message = "Playlist export is disabled (MusicEnricher:EnablePlaylistExport)." });

                // Run off the request path: a full export is dozens of sequential Spotify calls and
                // would blow past the gateway timeout. The export service self-serializes, so a second
                // click while one is running is a safe no-op.
                _ = Task.Run(async () =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var logger = loggerFactory.CreateLogger("PlaylistExportTrigger");
                    try
                    {
                        var export = scope.ServiceProvider.GetRequiredService<IPlaylistExportService>();
                        await export.RunExportAsync(CancellationToken.None);
                    }
                    catch (SpotifyNotConnectedException)
                    {
                        logger.LogInformation("Playlist export trigger skipped: Spotify not connected");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Manual playlist export run failed");
                    }
                });

                return Results.Json(new { queued = true }, statusCode: StatusCodes.Status202Accepted);
            })
            .WithName("RegenerateExportedPlaylists")
            .WithSummary("Re-export the on-disk M3U files for every subscribed Spotify collection.");

        return app;
    }

    private static string CollectionKey(ExportedPlaylistKind kind, string? spotifyPlaylistId)
        => $"{kind}\0{spotifyPlaylistId ?? string.Empty}";

    private static PlaylistCollectionDto ToDto(
        ExportedPlaylistKind kind, string? spotifyPlaylistId, string name, string? imageUrl, string? ownerName,
        int liveTrackTotal, ExportedPlaylist? row)
        => new(
            row?.Id,
            kind.ToString(),
            spotifyPlaylistId,
            string.IsNullOrWhiteSpace(name) ? row?.Name ?? string.Empty : name,
            imageUrl,
            ownerName,
            liveTrackTotal,
            row is not null,
            row?.MatchedTrackCount ?? 0,
            string.IsNullOrWhiteSpace(row?.FilePath) ? null : row!.FilePath,
            row?.LastGeneratedAtUtc);

    private static List<PlaylistCollectionDto> SubscribedOnly(IReadOnlyList<ExportedPlaylist> rows)
        => rows
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PlaylistCollectionDto(
                r.Id,
                r.Kind.ToString(),
                r.SpotifyPlaylistId,
                r.Name,
                null,
                null,
                r.SpotifyTrackTotal,
                true,
                r.MatchedTrackCount,
                string.IsNullOrWhiteSpace(r.FilePath) ? null : r.FilePath,
                r.LastGeneratedAtUtc))
            .ToList();
}

public sealed record PlaylistCollectionDto(
    int? Id,
    string Kind,
    string? SpotifyPlaylistId,
    string Name,
    string? ImageUrl,
    string? OwnerName,
    int SpotifyTrackTotal,
    bool Subscribed,
    int MatchedTrackCount,
    string? FilePath,
    DateTime? LastGeneratedAtUtc);

public sealed record PlaylistCollectionsResponse(
    bool SpotifyConnected,
    string? SpotifyError,
    IReadOnlyList<PlaylistCollectionDto> Collections);

public sealed record SubscribePlaylistRequest(string Kind, string? SpotifyPlaylistId, string? Name);
