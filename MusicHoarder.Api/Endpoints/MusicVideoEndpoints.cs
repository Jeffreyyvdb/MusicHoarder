using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Music video ("clip") endpoints: info + range-enabled stream for the player's video backdrop, and
/// owner-only fetch/offset/delete management. Reads rely on the per-user query filter (a foreign
/// song id resolves to 404) so the demo account can watch; mutations are owner-only.
/// </summary>
public static class MusicVideoEndpoints
{
    public static IEndpointRouteBuilder MapMusicVideoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/songs/{id:int}/video", GetVideoInfo)
            .WithName("GetSongVideo")
            .WithSummary("Sync + status info for the song's music video (404 when none is attached).")
            .WithTags("MusicVideos");
        app.MapGet("/songs/{id:int}/video/stream", StreamVideo)
            .WithName("StreamSongVideo")
            .WithSummary("Range-enabled mp4 stream of the song's music video, played muted behind the player.")
            .WithTags("MusicVideos");
        app.MapPost("/songs/{id:int}/video/fetch", FetchVideo)
            .WithName("FetchSongVideo")
            .WithSummary("Queue a background YouTube fetch of this song's music video (optional exact URL; otherwise searches by artist/title). Poll the info endpoint for progress.")
            .WithTags("MusicVideos")
            .RequireAdmin();
        app.MapPatch("/songs/{id:int}/video/offset", SetVideoOffset)
            .WithName("SetSongVideoOffset")
            .WithSummary("Manually nudge the audio↔video sync offset (videoTime = audioTime + offsetMs/1000), or reset to automatic re-alignment.")
            .WithTags("MusicVideos")
            .RequireAdmin();
        app.MapDelete("/songs/{id:int}/video", DeleteVideo)
            .WithName("DeleteSongVideo")
            .WithSummary("Remove the song's music video (deletes the downloaded file).")
            .WithTags("MusicVideos")
            .RequireAdmin();
        return app;
    }

    public record VideoInfoDto(
        string Status,
        int SyncOffsetMs,
        string SyncSource,
        double? SyncConfidence,
        int? DurationSeconds,
        string? YouTubeVideoId,
        DateTime FetchedAtUtc,
        string? LastError,
        bool FileMissing);

    /// <param name="includeDiagnostics">
    /// Whether <c>LastError</c> may be returned. Defaults to FALSE so callers are safe by
    /// omission — only pass true for a song the requester owns.
    ///
    /// <para>
    /// <c>LastError</c> is a yt-dlp stderr tail, which routinely embeds local filesystem paths and
    /// resolved URLs. It is actionable to whoever can act on it (the library owner, who gets a
    /// re-fetch button) and is pure internal detail to anyone else.
    /// </para>
    /// </param>
    // Internal (not private) so SharedLibraryEndpoints can serve the identical, path-free shape
    // for grant-scoped songs — same reuse contract as StreamVideoFile below.
    internal static VideoInfoDto ToDto(SongMusicVideo v, bool includeDiagnostics = false) => new(
        v.Status.ToString(),
        v.SyncOffsetMs,
        v.SyncSource.ToString(),
        v.SyncConfidence,
        v.DurationSeconds,
        v.YouTubeVideoId,
        v.FetchedAtUtc,
        includeDiagnostics ? v.LastError : null,
        // A Ready row whose mp4 vanished (volume moved, manual cleanup) would otherwise report
        // healthy while the stream endpoint 404s — the UI needs to offer a refetch, not a black
        // backdrop.
        FileMissing: v.Status == MusicVideoStatus.Ready
            && (v.FilePath is null || !File.Exists(v.FilePath)));

    /// <summary>
    /// The video row for a song the caller may read — their own, or one shared with them.
    ///
    /// <para>
    /// Two steps, and the order is the security property: authorize the SONG through
    /// <see cref="ILibraryScopeResolver"/> first, then read the video row with the tenancy filter
    /// bypassed. The bypass is required because <see cref="SongMusicVideo"/> is scoped by its
    /// parent song's owner, so a filtered read returns nothing for a grantee and every shared
    /// track reports "no video". It is safe only because the song was already authorized.
    /// </para>
    /// </summary>
    private static async Task<(SongMusicVideo? Video, bool IsSelf)> ResolveVideoAsync(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver, CancellationToken ct)
    {
        var found = await scopeResolver.ResolveSongAsync(db, id, ct);
        if (found is null) return (null, false);

        var video = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(v => v.SongId == found.Value.Song.Id, ct);
        return (video, found.Value.Slice.IsSelf);
    }

    internal static async Task<IResult> GetVideoInfo(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver, CancellationToken ct)
    {
        var (video, isSelf) = await ResolveVideoAsync(id, db, scopeResolver, ct);
        return video is null
            ? Results.NotFound(new { message = "No music video for this song." })
            : Results.Ok(ToDto(video, includeDiagnostics: isSelf));
    }

    internal static async Task<IResult> StreamVideo(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver, CancellationToken ct)
    {
        var (video, _) = await ResolveVideoAsync(id, db, scopeResolver, ct);
        return StreamVideoFile(video);
    }

    /// <summary>
    /// Range-enabled mp4 stream for an already-authorized video row (shared with the anonymous
    /// share surface); 404 unless the video is Ready with an existing file.
    /// </summary>
    internal static IResult StreamVideoFile(SongMusicVideo? video)
    {
        if (video is not { Status: MusicVideoStatus.Ready, FilePath: not null } || !File.Exists(video.FilePath))
            return Results.NotFound(new { message = "No music video file for this song." });

        var stream = new FileStream(
            video.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true);

        return Results.Stream(stream, contentType: "video/mp4", enableRangeProcessing: true);
    }

    public record FetchVideoRequest(string? Url);

    internal static async Task<IResult> FetchVideo(
        int id, FetchVideoRequest? request, MusicHoarderDbContext db, MusicVideoChannel channel, CancellationToken ct)
    {
        var song = await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        string? pinnedUrl = null;
        string? pinnedVideoId = null;
        if (!string.IsNullOrWhiteSpace(request?.Url))
        {
            if (!ImportUrlParser.TryParse(request.Url, out var kind, out var videoId) || kind != ImportUrlKind.YouTube)
                return Results.BadRequest(new { message = "Not a recognizable YouTube URL." });
            pinnedUrl = ImportUrlParser.YouTubeWatchUrl(videoId);
            pinnedVideoId = videoId;
        }

        var video = await db.SongMusicVideos
            .FirstOrDefaultAsync(v => v.SongId == id, ct);
        if (video is null)
        {
            video = new SongMusicVideo { SongId = id };
            db.SongMusicVideos.Add(video);
        }
        else if (video.Status == MusicVideoStatus.Fetching)
        {
            // Already queued/in-flight — don't stack a second fetch.
            return Results.Accepted(value: ToDto(video, includeDiagnostics: true));
        }

        video.Status = MusicVideoStatus.Fetching;
        video.LastError = null;
        // Stamp the pinned id so a restart's re-enqueue (which only has the row) retries the same video.
        if (pinnedVideoId is not null)
            video.YouTubeVideoId = pinnedVideoId;
        await db.SaveChangesAsync(ct);

        channel.Enqueue(new MusicVideoWorkItem(id, MusicVideoWorkKind.Fetch, pinnedUrl));
        return Results.Accepted(value: ToDto(video, includeDiagnostics: true));
    }

    public record SetOffsetRequest(int? OffsetMs, bool? ResetToAuto);

    internal static async Task<IResult> SetVideoOffset(
        int id, SetOffsetRequest request, MusicHoarderDbContext db, MusicVideoChannel channel, CancellationToken ct)
    {
        var video = await db.SongMusicVideos
            .FirstOrDefaultAsync(v => v.SongId == id && v.Song.DeletedAtUtc == null, ct);
        if (video is null)
            return Results.NotFound(new { message = "No music video for this song." });

        if (request.ResetToAuto == true)
        {
            video.SyncOffsetMs = 0;
            video.SyncSource = MusicVideoSyncSource.Unaligned;
            video.SyncConfidence = null;
            await db.SaveChangesAsync(ct);
            channel.Enqueue(new MusicVideoWorkItem(id, MusicVideoWorkKind.Align));
            return Results.Ok(ToDto(video, includeDiagnostics: true));
        }

        if (request.OffsetMs is not { } offsetMs)
            return Results.BadRequest(new { message = "offsetMs (or resetToAuto) is required." });

        // ±10 min bounds out fat-fingered values while allowing any realistic intro/edit shift.
        video.SyncOffsetMs = Math.Clamp(offsetMs, -600_000, 600_000);
        video.SyncSource = MusicVideoSyncSource.Manual;
        video.SyncConfidence = null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(video, includeDiagnostics: true));
    }

    internal static async Task<IResult> DeleteVideo(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var video = await db.SongMusicVideos
            .FirstOrDefaultAsync(v => v.SongId == id, ct);
        if (video is null)
            return Results.NotFound(new { message = "No music video for this song." });

        // The file and its thumbnail are managed artifacts under the videos directory — safe to
        // remove with the row.
        if (video.FilePath is not null)
        {
            foreach (var path in new[] { video.FilePath, Path.ChangeExtension(video.FilePath, ".jpg") })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { /* orphan cleanup is best-effort */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        db.SongMusicVideos.Remove(video);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
