using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Soulseek;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Owner-only surface for the Soulseek integration: connection status plus the manual
/// quality-upgrade queue ("find a better copy of this track/album on Soulseek").
/// </summary>
public static class SoulseekEndpoints
{
    public record CreateUpgradeRequest(int? SongId, string? Artist, string? Album);

    public static void MapSoulseekEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/soulseek").WithTags("Soulseek").RequireOwner();

        group.MapGet("/status", Status);
        group.MapPost("/upgrades", CreateUpgrades);
        group.MapGet("/upgrades", ListUpgrades);
        group.MapDelete("/upgrades/{id:int}", CancelUpgrade);
    }

    private static async Task<IResult> Status(
        IOptionsMonitor<SlskdOptions> options, ISlskdClient client, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            return Results.Ok(new { configured = false, connected = false, version = (string?)null });

        var state = await client.GetApplicationStateAsync(ct);
        return Results.Ok(new
        {
            configured = true,
            connected = state?.IsConnected ?? false,
            version = state?.Version,
        });
    }

    /// <summary>
    /// Queues upgrade request(s): one song by id, or every eligible track of an album
    /// (artist+album, mirroring the album re-tag endpoint's addressing). 409 when a song already
    /// has an active request; 400 when slskd isn't configured.
    /// </summary>
    private static async Task<IResult> CreateUpgrades(
        CreateUpgradeRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        QualityUpgradeChannel channel,
        IEnumerable<IUpgradeProvider> upgradeProviders,
        CancellationToken ct)
    {
        // Need at least one configured provider that can upgrade a lossy file (slskd or spotiflac);
        // otherwise the request would just sit and fail.
        var lossyProbe = new UpgradeFloor(AudioCodecTier.Lossy, AudioQuality.Score(".mp3", 0), null);
        if (!upgradeProviders.Any(p => p.CanUpgrade(lossyProbe)))
            return Results.BadRequest(new { error = "no_upgrade_provider_configured" });

        var ownerId = currentUser.User!.Id;
        List<int> songIds;
        if (body.SongId is { } songId)
        {
            songIds = [songId];
        }
        else if (!string.IsNullOrWhiteSpace(body.Artist) && !string.IsNullOrWhiteSpace(body.Album))
        {
            songIds = await db.Songs
                .Where(s => s.AlbumArtist == body.Artist && s.Album == body.Album
                    && !s.IsDuplicate && !s.IsSynthetic)
                .Select(s => s.Id)
                .ToListAsync(ct);
            if (songIds.Count == 0)
                return Results.NotFound(new { error = "album_not_found" });
        }
        else
        {
            return Results.BadRequest(new { error = "song_id_or_artist_album_required" });
        }

        // One active request per song: re-queuing a track mid-search only wastes network etiquette.
        var active = await db.UpgradeRequests
            .Where(r => songIds.Contains(r.SongId)
                && (r.Status == UpgradeRequestStatus.Queued
                    || r.Status == UpgradeRequestStatus.Searching
                    || r.Status == UpgradeRequestStatus.Downloading
                    || r.Status == UpgradeRequestStatus.AwaitingIngest))
            .Select(r => r.SongId)
            .ToListAsync(ct);
        if (body.SongId is not null && active.Count > 0)
            return Results.Conflict(new { error = "upgrade_already_active", songId = body.SongId });

        var toQueue = songIds.Except(active).ToList();
        var now = DateTime.UtcNow;
        var requests = toQueue.Select(id => new UpgradeRequest
        {
            SongId = id,
            OwnerUserId = ownerId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }).ToList();
        db.UpgradeRequests.AddRange(requests);
        await db.SaveChangesAsync(ct);

        foreach (var request in requests)
            channel.Enqueue(request.Id);

        return Results.Ok(new { queued = requests.Count, skippedActive = active.Count });
    }

    private static async Task<IResult> ListUpgrades(
        MusicHoarderDbContext db, string? status, int? limit, CancellationToken ct)
    {
        var query = db.UpgradeRequests.Include(r => r.Song).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<UpgradeRequestStatus>(status, ignoreCase: true, out var parsed))
            query = query.Where(r => r.Status == parsed);

        var items = await query
            .OrderByDescending(r => r.Id)
            .Take(Math.Clamp(limit ?? 100, 1, 500))
            .Select(r => new
            {
                r.Id,
                r.SongId,
                songArtist = r.Song!.Artist,
                songTitle = r.Song.Title,
                songExtension = r.Song.Extension,
                songBitrate = r.Song.Bitrate,
                status = r.Status.ToString(),
                r.CandidateQualityScore,
                r.CandidateInfoJson,
                r.Error,
                r.CreatedAtUtc,
                r.UpdatedAtUtc,
                r.CompletedAtUtc,
            })
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    /// <summary>Cancels a request that hasn't started transferring yet (Queued/Searching only —
    /// the worker re-checks status before downloading).</summary>
    private static async Task<IResult> CancelUpgrade(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var request = await db.UpgradeRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return Results.NotFound();
        if (request.Status is not (UpgradeRequestStatus.Queued or UpgradeRequestStatus.Searching))
            return Results.Conflict(new { error = "not_cancellable", status = request.Status.ToString() });

        request.MarkTerminal(UpgradeRequestStatus.Cancelled);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { cancelled = true });
    }
}
