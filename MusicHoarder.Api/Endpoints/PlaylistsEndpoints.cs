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

        group.MapGet("/", async (MusicHoarderDbContext db, CancellationToken ct) =>
            {
                var playlists = await db.ExportedPlaylists
                    .AsNoTracking()
                    // Liked Songs (Kind 0) first, then playlists alphabetically.
                    .OrderBy(e => e.Kind)
                    .ThenBy(e => e.Name)
                    .Select(e => new ExportedPlaylistDto(
                        e.Id,
                        e.Kind.ToString(),
                        e.SpotifyPlaylistId,
                        e.Name,
                        e.FilePath,
                        e.SpotifyTrackTotal,
                        e.MatchedTrackCount,
                        e.LastGeneratedAtUtc))
                    .ToListAsync(ct);

                return Results.Ok(new ExportedPlaylistsResponse(playlists));
            })
            .WithName("GetExportedPlaylists")
            .WithSummary("Lists the owner's exported Spotify playlists with M3U coverage.");

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
            .WithSummary("Regenerate the on-disk M3U playlist files from the owner's Spotify library.");

        return app;
    }
}

public sealed record ExportedPlaylistDto(
    int Id,
    string Kind,
    string? SpotifyPlaylistId,
    string Name,
    string FilePath,
    int SpotifyTrackTotal,
    int MatchedTrackCount,
    DateTime? LastGeneratedAtUtc);

public sealed record ExportedPlaylistsResponse(IReadOnlyList<ExportedPlaylistDto> Playlists);
