using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Chat;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Shareable song/album links. Two surfaces with very different auth postures:
///
/// <c>/api/shares</c> (owner-only) — create, list, and revoke share links, and read how often each
/// was opened and played.
///
/// <c>/api/share/{token}</c> (anonymous — allowlisted in <c>RequireAuthMiddleware</c>) — what the
/// link recipient hits. The token is the whole capability: every handler resolves it to a
/// <see cref="SongShare"/> row via <c>IgnoreQueryFilters()</c> (an anonymous request's EF filter
/// would return zero rows) and then re-scopes every song read to the share's own
/// <c>OwnerUserId</c> + song/album membership, so a token can never reach beyond what was shared.
/// </summary>
public static class SharesEndpoints
{
    public static IEndpointRouteBuilder MapSharesEndpoints(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/shares").WithTags("Shares").RequireAdmin();
        owner.MapPost("", CreateShare)
            .WithName("CreateShare")
            .WithSummary("Create (or return the existing) share link for a song or its whole album.");
        owner.MapGet("", ListShares)
            .WithName("ListShares")
            .WithSummary("List the owner's active share links.");
        owner.MapDelete("/{id:int}", RevokeShare)
            .WithName("RevokeShare")
            .WithSummary("Revoke a share link; the URL stops working immediately.");
        owner.MapGet("/stats", ListShareStats)
            .WithName("ListShareStats")
            .WithSummary("Every share link the owner made, active and revoked, with open/visitor/play totals.");
        owner.MapGet("/{id:int}/stats", GetShareStats)
            .WithName("GetShareStats")
            .WithSummary("One share link's totals, opens per day, where visitors came from, and plays per track.");

        var pub = app.MapGroup("/api/share").WithTags("Shares");
        pub.MapGet("/{token}", GetSharePayload)
            .WithName("GetSharePayload")
            .WithSummary("Anonymous: the shared song/album's playable tracklist and display metadata.");
        pub.MapGet("/{token}/songs/{id:int}/stream", StreamSharedSong)
            .WithName("StreamSharedSong");
        pub.MapGet("/{token}/songs/{id:int}/cover", GetSharedSongCover)
            .WithName("GetSharedSongCover");
        pub.MapGet("/{token}/songs/{id:int}/lyrics", GetSharedSongLyrics)
            .WithName("GetSharedSongLyrics");
        pub.MapGet("/{token}/songs/{id:int}/video/stream", StreamSharedSongVideo)
            .WithName("StreamSharedSongVideo");
        pub.MapPost("/{token}/visit", RecordShareVisit)
            .WithName("RecordShareVisit")
            .WithSummary("Anonymous beacon: the share page was opened. Counted by ShareVisitTracker.");
        pub.MapPost("/{token}/songs/{id:int}/play", RecordSharePlay)
            .WithName("RecordSharePlay")
            .WithSummary("Anonymous beacon: a shared track started playing. Counted by ShareVisitTracker.");

        return app;
    }

    public sealed record CreateShareRequest(int SongId, string? Scope);

    internal static async Task<IResult> CreateShare(
        CreateShareRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var scope = SongShareFactory.ParseScope(body.Scope);

        // Query filter scopes to the current user, so this doubles as the ownership check.
        var song = await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == body.SongId && s.DeletedAtUtc == null, ct);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {body.SongId} not found." });

        var share = await SongShareFactory.GetOrCreateAsync(db, currentUser.UserId, body.SongId, scope, DateTime.UtcNow, ct);
        return Results.Ok(ToShareView(share, song));
    }

    internal static async Task<IResult> ListShares(MusicHoarderDbContext db, CancellationToken ct)
    {
        var shares = await db.SongShares.AsNoTracking()
            .Where(s => s.RevokedAtUtc == null)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new
            {
                s.Id,
                s.Token,
                Scope = s.Scope.ToString(),
                s.SongId,
                s.CreatedAtUtc,
                Title = s.Song!.Title ?? s.Song.FileName,
                s.Song.Artist,
                s.Song.Album,
            })
            .ToListAsync(ct);

        return Results.Ok(shares);
    }

    internal static async Task<IResult> RevokeShare(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var share = await db.SongShares.FirstOrDefaultAsync(s => s.Id == id && s.RevokedAtUtc == null, ct);
        if (share is null)
            return Results.NotFound(new { message = $"Share with id {id} not found." });

        share.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── Stats ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A share link with its all-time counts — one row of the Share links page.</summary>
    public sealed record ShareStatsRow(
        int Id,
        string Token,
        string Scope,
        int SongId,
        DateTime CreatedAtUtc,
        DateTime? RevokedAtUtc,
        string Title,
        string? Artist,
        string? Album,
        int Views,
        int Visitors,
        int Plays,
        DateTime? LastViewedAtUtc);

    public sealed record ShareDailyPoint(DateOnly Date, int Views, int Visitors, int Plays);

    /// <summary>Opens by where they came from; a null <c>Source</c> is "direct or unknown".</summary>
    public sealed record ShareSourceCount(string? Source, int Views);

    public sealed record ShareTrackPlays(int SongId, string Title, int Plays);

    public sealed record ShareStatsDetail(
        ShareStatsRow Share,
        IReadOnlyList<ShareDailyPoint> Daily,
        IReadOnlyList<ShareSourceCount> Sources,
        IReadOnlyList<ShareTrackPlays> Tracks);

    private sealed record ShareTotals(int Views, int Plays, int Visitors, DateTime? LastViewedAtUtc)
    {
        public static readonly ShareTotals None = new(0, 0, 0, null);
    }

    /// <summary>
    /// The shape both stats reads share, before the counts are joined on. Revoked links stay listed:
    /// the numbers of a link you have since killed are still the answer to "did anyone open it".
    /// </summary>
    private static IQueryable<ShareStatsRow> ShareRows(MusicHoarderDbContext db, int? shareId = null) =>
        db.SongShares.AsNoTracking()
            .Where(s => shareId == null || s.Id == shareId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ThenByDescending(s => s.Id)
            .Select(s => new ShareStatsRow(
                s.Id,
                s.Token,
                s.Scope.ToString(),
                s.SongId,
                s.CreatedAtUtc,
                s.RevokedAtUtc,
                s.Song!.Title ?? s.Song.FileName,
                s.Song.Artist,
                s.Song.Album,
                0,
                0,
                0,
                null));

    internal static async Task<IResult> ListShareStats(MusicHoarderDbContext db, CancellationToken ct)
    {
        var rows = await ShareRows(db).ToListAsync(ct);
        var totals = await LoadTotalsAsync(db, shareId: null, ct);
        return Results.Ok(rows.Select(r => WithTotals(r, totals)).ToList());
    }

    /// <param name="days">How many days the daily series covers, ending today (1–365, default 30).
    /// It never starts before the day the link was made.</param>
    /// <param name="tzOffsetMinutes">The caller's <c>Date.getTimezoneOffset()</c> (UTC minus local,
    /// in minutes), so "a day" is the owner's day rather than UTC's.</param>
    internal static async Task<IResult> GetShareStats(
        int id,
        MusicHoarderDbContext db,
        int? days,
        int? tzOffsetMinutes,
        CancellationToken ct)
    {
        // The query filter scopes to the caller's own links, so this doubles as the ownership check.
        var row = await ShareRows(db, id).FirstOrDefaultAsync(ct);
        if (row is null)
            return Results.NotFound(new { message = $"Share with id {id} not found." });

        var totals = await LoadTotalsAsync(db, id, ct);
        var share = WithTotals(row, totals);

        var visits = db.ShareVisits.AsNoTracking().Where(v => v.ShareId == id);

        var offset = TimeSpan.FromMinutes(Math.Clamp(tzOffsetMinutes ?? 0, -14 * 60, 14 * 60));
        var span = Math.Clamp(days ?? 30, 1, 365);
        var today = DateOnly.FromDateTime(DateTime.UtcNow - offset);
        var created = DateOnly.FromDateTime(row.CreatedAtUtc - offset);
        var first = today.AddDays(1 - span);
        if (created > first)
            first = created <= today ? created : today;
        var windowStartUtc = first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + offset;

        var recent = await visits
            .Where(v => v.OccurredAtUtc >= windowStartUtc)
            .Select(v => new { v.Kind, v.OccurredAtUtc, v.VisitorKey })
            .ToListAsync(ct);
        var byDay = recent.ToLookup(v => DateOnly.FromDateTime(v.OccurredAtUtc - offset));
        var daily = new List<ShareDailyPoint>();
        for (var day = first; day <= today; day = day.AddDays(1))
        {
            var hits = byDay[day].ToList();
            daily.Add(new ShareDailyPoint(
                day,
                hits.Count(v => v.Kind == ShareVisitKind.View),
                hits.Select(v => v.VisitorKey).Distinct().Count(),
                hits.Count(v => v.Kind == ShareVisitKind.Play)));
        }

        var sources = (await visits
                .Where(v => v.Kind == ShareVisitKind.View)
                .GroupBy(v => v.Source)
                .Select(g => new { Source = g.Key, Views = g.Count() })
                .ToListAsync(ct))
            .OrderByDescending(s => s.Views)
            .ThenBy(s => s.Source is null)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ShareSourceCount(s.Source, s.Views))
            .ToList();

        var plays = await visits
            .Where(v => v.Kind == ShareVisitKind.Play && v.SongId != null)
            .GroupBy(v => v.SongId!.Value)
            .Select(g => new { SongId = g.Key, Plays = g.Count() })
            .ToListAsync(ct);
        var playedIds = plays.Select(p => p.SongId).ToList();
        var titles = await db.Songs.AsNoTracking()
            .Where(s => playedIds.Contains(s.Id))
            .Select(s => new { s.Id, Title = s.Title ?? s.FileName })
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct);
        var tracks = plays
            .Select(p => new ShareTrackPlays(p.SongId, titles.GetValueOrDefault(p.SongId) ?? $"Song {p.SongId}", p.Plays))
            .OrderByDescending(t => t.Plays)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(new ShareStatsDetail(share, daily, sources, tracks));
    }

    /// <summary>All-time counts per share — every share of the caller's, or just <paramref name="shareId"/>.</summary>
    private static async Task<Dictionary<int, ShareTotals>> LoadTotalsAsync(
        MusicHoarderDbContext db,
        int? shareId,
        CancellationToken ct)
    {
        var visits = db.ShareVisits.AsNoTracking();
        if (shareId is { } only)
            visits = visits.Where(v => v.ShareId == only);

        var byKind = await visits
            .GroupBy(v => new { v.ShareId, v.Kind })
            .Select(g => new { g.Key.ShareId, g.Key.Kind, Count = g.Count(), Last = g.Max(v => v.OccurredAtUtc) })
            .ToListAsync(ct);
        var visitors = await visits
            .Select(v => new { v.ShareId, v.VisitorKey })
            .Distinct()
            .GroupBy(v => v.ShareId)
            .Select(g => new { ShareId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(v => v.ShareId, v => v.Count, ct);

        return byKind
            .GroupBy(k => k.ShareId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var views = g.FirstOrDefault(k => k.Kind == ShareVisitKind.View);
                    var plays = g.FirstOrDefault(k => k.Kind == ShareVisitKind.Play);
                    return new ShareTotals(
                        views?.Count ?? 0,
                        plays?.Count ?? 0,
                        visitors.GetValueOrDefault(g.Key),
                        views?.Last);
                });
    }

    private static ShareStatsRow WithTotals(ShareStatsRow row, IReadOnlyDictionary<int, ShareTotals> totals)
    {
        var t = totals.GetValueOrDefault(row.Id) ?? ShareTotals.None;
        return row with { Views = t.Views, Visitors = t.Visitors, Plays = t.Plays, LastViewedAtUtc = t.LastViewedAtUtc };
    }

    private static object ToShareView(SongShare share, SongMetadata song) => new
    {
        share.Id,
        share.Token,
        Scope = share.Scope.ToString(),
        share.SongId,
        share.CreatedAtUtc,
        Title = song.Title ?? song.FileName,
        song.Artist,
        song.Album,
    };

    // ── Anonymous surface ───────────────────────────────────────────────────────────────────

    internal static async Task<IResult> GetSharePayload(string token, MusicHoarderDbContext db, CancellationToken ct)
    {
        var share = await ResolveShareAsync(db, token, ct);
        if (share is null)
            return ShareNotFound();

        var songs = await LoadSongsInScopeAsync(db, share, ct);
        if (songs.Count == 0)
            return ShareNotFound();

        var shared = songs.First(s => s.Id == share.SongId);

        // Ready music videos ride along so the share page can play them as the backdrop. The sync
        // offset is the owner's curated alignment (audio is the master clock: videoTime =
        // audioTime + offsetMs/1000) — anonymous viewers get it read-only.
        var songIds = songs.Select(s => s.Id).ToList();
        var videos = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => songIds.Contains(v.SongId) && v.Status == MusicVideoStatus.Ready && v.FilePath != null)
            .ToDictionaryAsync(v => v.SongId, ct);

        return Results.Ok(new
        {
            Scope = share.Scope.ToString(),
            SharedSongId = share.SongId,
            Album = new
            {
                Title = shared.Album,
                Artist = shared.AlbumArtist ?? shared.Artist,
                shared.Year,
            },
            Tracks = songs.Select(s =>
            {
                videos.TryGetValue(s.Id, out var video);
                return new
                {
                    s.Id,
                    Title = s.Title ?? s.FileName,
                    s.Artist,
                    s.TrackNumber,
                    s.DiscNumber,
                    DurationMs = s.DurationMs ?? s.DurationSeconds * 1000,
                    s.HasCoverArt,
                    HasSyncedLyrics = !string.IsNullOrWhiteSpace(s.DisplaySyncedLyrics),
                    HasPlainLyrics = !string.IsNullOrWhiteSpace(s.DisplayPlainLyrics),
                    IsInstrumental = s.IsInstrumental == true,
                    HasVideo = video is not null,
                    VideoOffsetMs = video?.SyncOffsetMs,
                    VideoDurationSeconds = video?.DurationSeconds,
                    // The bars baked into the frame, which the page's backdrop crops (see VideoInfoDto).
                    VideoLetterbox = video?.LetterboxFraction,
                    VideoPillarbox = video?.PillarboxFraction,
                };
            }),
        });
    }

    internal static async Task<IResult> StreamSharedSong(
        string token, int id, string? format, MusicHoarderDbContext db, IPcmDecoder decoder, CancellationToken ct)
    {
        var song = await ResolveSongInScopeAsync(db, token, id, ct);
        return song is null ? ShareNotFound() : await SongsEndpoints.StreamSongFileAsync(song, format, decoder, ct);
    }

    internal static async Task<IResult> GetSharedSongCover(
        string token,
        int id,
        MusicHoarderDbContext db,
        ICoverArtResolver coverArtResolver,
        ICoverThumbnailService thumbnails,
        HttpContext http,
        int? size,
        CancellationToken ct)
    {
        var song = await ResolveSongInScopeAsync(db, token, id, ct);
        if (song is null)
            return ShareNotFound();

        return await SongsEndpoints.ServeCoverAsync(song, coverArtResolver, thumbnails, http, size);
    }

    internal static async Task<IResult> GetSharedSongLyrics(string token, int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var song = await ResolveSongInScopeAsync(db, token, id, ct);
        if (song is null)
            return ShareNotFound();

        // Display* (not Effective*): the share page shows what the in-app viewer shows, including
        // the fall-back to an AI transcription when LRCLIB found nothing — without changing what
        // the library builder embeds into files.
        //
        // Pronunciation/translation ride along only while FRESH: a stale doc was generated from
        // different lyrics, so stacking it under the current lines would misalign for anonymous
        // viewers who have no Regenerate button.
        var translationFresh =
            song.LyricsTranslationStatus == LyricsTranslationStatus.Completed
            && !song.IsLyricsTranslationStale;
        return Results.Ok(new
        {
            song.Id,
            Synced = song.DisplaySyncedLyrics,
            Plain = song.DisplayPlainLyrics,
            // The AI disclosure travels with the lyrics onto the public share page — anyone reading these
            // words is entitled to know a machine wrote or re-timed them, signed in or not.
            LyricsProvenance = song.LyricsProvenance.ToString(),
            IsInstrumental = song.IsInstrumental == true,
            RomanizedSynced = translationFresh ? song.RomanizedSyncedLyrics : null,
            RomanizedPlain = translationFresh ? song.RomanizedPlainLyrics : null,
            TranslatedSynced = translationFresh ? song.TranslatedSyncedLyrics : null,
            TranslatedPlain = translationFresh ? song.TranslatedPlainLyrics : null,
            DetectedLanguage = translationFresh ? song.DetectedLyricsLanguage : null,
        });
    }

    internal static async Task<IResult> StreamSharedSongVideo(string token, int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var song = await ResolveSongInScopeAsync(db, token, id, ct);
        if (song is null)
            return ShareNotFound();

        var video = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(v => v.SongId == song.Id, ct);
        return MusicVideoEndpoints.StreamVideoFile(video);
    }

    // ── Beacons ─────────────────────────────────────────────────────────────────────────────

    /// <param name="Referrer">The share page's <c>document.referrer</c>, when it came from another site.</param>
    public sealed record ShareVisitRequest(string? Referrer);

    /// <summary>
    /// The share page reports its own open. A beacon rather than a count in <see cref="GetSharePayload"/>
    /// because the payload is also fetched by link-preview crawlers (the page renders server-side for
    /// its og-tags), and a crawler runs no script. 204 whether or not the visit was counted, so the
    /// response says nothing about the owner, the de-duplication or the cap.
    /// </summary>
    internal static async Task<IResult> RecordShareVisit(
        string token,
        ShareVisitRequest? body,
        HttpContext http,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ShareVisitTracker tracker,
        ChatService chat,
        ILogger<ChatService> logger,
        CancellationToken ct)
    {
        var share = await ResolveShareAsync(db, token, ct);
        if (share is null)
            return ShareNotFound();

        var visitor = ShareVisitor.From(http, currentUser.User, body?.Referrer);
        await tracker.TryRecordAsync(db, share, ShareVisitKind.View, songId: null, visitor, ct);

        // Opened by someone signed in to this instance: file it in their chat with the owner, the way
        // Spotify, TikTok and YouTube do. Best effort — the beacon's answer never depends on it — and
        // only for the visitor: the owner is not told who opened the link (see RecordShareOpenedAsync).
        if (currentUser.User is { IsDemo: false } signedIn && signedIn.Id != share.OwnerUserId)
        {
            try
            {
                await chat.RecordShareOpenedAsync(share, signedIn.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not put share {ShareId} in the visitor's chats", share.Id);
            }
        }
        return Results.NoContent();
    }

    /// <summary>A shared track started playing. Only tracks the share covers are counted.</summary>
    internal static async Task<IResult> RecordSharePlay(
        string token,
        int id,
        HttpContext http,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ShareVisitTracker tracker,
        CancellationToken ct)
    {
        var share = await ResolveShareAsync(db, token, ct);
        if (share is null)
            return ShareNotFound();
        var songs = await LoadSongsInScopeAsync(db, share, ct);
        if (songs.All(s => s.Id != id))
            return ShareNotFound();

        var visitor = ShareVisitor.From(http, currentUser.User, referrer: null);
        await tracker.TryRecordAsync(db, share, ShareVisitKind.Play, id, visitor, ct);
        return Results.NoContent();
    }

    /// <summary>Uniform 404 for unknown, revoked, and out-of-scope requests — no oracle for probing.</summary>
    private static IResult ShareNotFound() =>
        Results.NotFound(new { message = "This share link does not exist or has been revoked." });

    private static async Task<SongShare?> ResolveShareAsync(MusicHoarderDbContext db, string token, CancellationToken ct) =>
        await db.SongShares.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token && s.RevokedAtUtc == null, ct);

    /// <summary>
    /// Every song the share's token may touch, in tracklist order. Song scope is just the shared
    /// row; Album scope is the owner's tracks carrying the same (album-artist, album) tags — the
    /// same grouping the album pages use. Year is deliberately not matched: per-track enrichment
    /// can leave one album's tracks with inconsistent years (see CLAUDE.md).
    /// </summary>
    private static async Task<List<SongMetadata>> LoadSongsInScopeAsync(
        MusicHoarderDbContext db,
        SongShare share,
        CancellationToken ct)
    {
        var shared = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == share.SongId && s.OwnerUserId == share.OwnerUserId && s.DeletedAtUtc == null,
                ct);
        if (shared is null)
            return [];

        if (share.Scope == ShareScope.Song || string.IsNullOrWhiteSpace(shared.Album))
            return [shared];

        var albumLower = shared.Album!.ToLower();
        var artistLower = ((shared.AlbumArtist ?? shared.Artist) ?? "").ToLower();

        var tracks = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OwnerUserId == share.OwnerUserId
                && s.DeletedAtUtc == null
                && !s.IsDuplicate
                && s.Album != null && s.Album.ToLower() == albumLower
                && ((s.AlbumArtist ?? s.Artist) ?? "").ToLower() == artistLower)
            .ToListAsync(ct);

        // The shared song must stay reachable even if it is itself flagged a duplicate.
        if (tracks.All(s => s.Id != shared.Id))
            tracks.Add(shared);

        return tracks
            .OrderBy(s => s.DiscNumber ?? 1)
            .ThenBy(s => s.TrackNumber ?? int.MaxValue)
            .ThenBy(s => s.Title)
            .ToList();
    }

    private static async Task<SongMetadata?> ResolveSongInScopeAsync(
        MusicHoarderDbContext db,
        string token,
        int songId,
        CancellationToken ct)
    {
        var share = await ResolveShareAsync(db, token, ct);
        if (share is null)
            return null;

        var songs = await LoadSongsInScopeAsync(db, share, ct);
        return songs.FirstOrDefault(s => s.Id == songId);
    }
}
