using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Navidrome;

/// <summary>
/// Reconciles song likes two ways with Navidrome's Subsonic "starred" state. The merge is a
/// three-way boolean merge against <see cref="SongMetadata.LikeLastSyncedValue"/> (the value agreed
/// at the last sync): whichever side differs from that base is the side that changed, and it wins.
/// For a boolean this is total and unambiguous — if both sides had changed from the base they would
/// now be equal (agreement), so there is never a genuine two-sided conflict. Matching is
/// path/MBID/fuzzy via <see cref="NavidromeSongIndex"/>; the same relative path can exist in several
/// Navidrome libraries (source + built copies), so a like is applied to <b>every</b> matching copy.
/// </summary>
public sealed class NavidromeLikeReconciler(
    MusicHoarderDbContext db,
    INavidromeClient client,
    IOwnerLookupService ownerLookup,
    IOptionsMonitor<NavidromeOptions> navOptions,
    IOptionsMonitor<MusicEnricherOptions> enricherOptions,
    ILogger<NavidromeLikeReconciler> logger)
{
    private const int MaxSongs = 200_000;

    private sealed record MhLikeRow(
        int Id, string? SourcePath, string? DestinationPath, string? MusicBrainzId,
        string? Artist, string? Title, int? DurationSeconds, bool IsLiked, bool? LikeLastSyncedValue);

    /// <summary>
    /// Full two-way sweep: pull Navidrome stars into MH likes and push MH likes out as stars, per the
    /// three-way merge. Returns a small summary for logging/observability.
    /// </summary>
    public async Task<ReconcileSummary> ReconcileAllAsync(CancellationToken ct)
    {
        var opts = navOptions.CurrentValue;
        if (!opts.IsConfigured)
            return ReconcileSummary.Empty;

        var (sourceDir, destDir) = Dirs();
        var ownerId = ownerLookup.OwnerUserId;

        var starred = await client.GetStarredSongsAsync(ct);
        var starredIndex = new NavidromeSongIndex(starred, opts.FuzzyDurationToleranceSeconds);

        var rows = await LoadRowsAsync(ownerId, ct);

        // Decide per song (pure, no I/O yet).
        var pulls = new List<(int Id, bool Liked)>();
        var baseOnly = new List<(int Id, bool Liked)>();
        var pushStar = new List<(int Id, LikeMatchKey Key, string Query)>();
        var pushUnstar = new List<(int Id, IReadOnlyList<NavidromeSong> Matches)>();

        foreach (var row in rows)
        {
            var key = BuildKey(row, sourceDir, destDir);
            var matches = starredIndex.Find(key);
            var navStarred = matches.Count > 0;
            var mh = row.IsLiked;
            var mergeBase = row.LikeLastSyncedValue;

            if (mh == navStarred)
            {
                if (mergeBase != mh) baseOnly.Add((row.Id, mh)); // agree on value, just record the base
                continue;
            }

            // Disagreement: exactly one side differs from the base (see class remarks).
            var mhChanged = mergeBase is null ? mh : mh != mergeBase.Value;
            if (mhChanged)
            {
                if (mh) pushStar.Add((row.Id, key, BuildSearchQuery(row)));
                else pushUnstar.Add((row.Id, matches));
            }
            else
            {
                pulls.Add((row.Id, navStarred));
            }
        }

        // Apply Navidrome side-effects; only songs whose remote op succeeds get their base advanced.
        var newBase = new Dictionary<int, bool>();
        var resolvedNavId = new Dictionary<int, string>();
        foreach (var (id, liked) in baseOnly) newBase[id] = liked;
        foreach (var (id, liked) in pulls) newBase[id] = liked;

        var starOps = 0;
        var unstarOps = 0;
        foreach (var (id, key, query) in pushStar)
        {
            var matches = await ResolveMatchesAsync(key, query, opts, ct);
            if (matches.Count == 0)
            {
                logger.LogDebug("No Navidrome match to star for song {SongId} (query {Query})", id, query);
                continue; // leave base unchanged → retried next sweep once the track exists in Navidrome
            }
            if (await ApplyStarAsync(matches, star: true, ct))
            {
                newBase[id] = true;
                resolvedNavId[id] = matches[0].Id;
                starOps += matches.Count;
            }
        }
        foreach (var (id, matches) in pushUnstar)
        {
            if (matches.Count == 0) { newBase[id] = false; continue; } // nothing starred remotely — already in the target state
            if (await ApplyStarAsync(matches, star: false, ct))
            {
                newBase[id] = false;
                unstarOps += matches.Count;
            }
        }

        await PersistAsync(pulls, newBase, resolvedNavId, ownerId, ct);

        var summary = new ReconcileSummary(rows.Count, starred.Count, pulls.Count, starOps, unstarOps);
        if (summary.HasWork)
            logger.LogInformation(
                "Navidrome like sweep: {Songs} songs, {Starred} starred; pulled {Pulls}, starred {Stars}, unstarred {Unstars}",
                summary.SongsConsidered, summary.RemoteStarred, summary.Pulled, summary.Starred, summary.Unstarred);
        return summary;
    }

    /// <summary>
    /// Immediate one-song push (MH → Navidrome) after the user toggles a like, for snappy sync without
    /// waiting for the sweep. Applies MH's current <see cref="SongMetadata.IsLiked"/> to every matching
    /// Navidrome copy and advances the merge base. The sweep still owns the Navidrome → MH direction.
    /// </summary>
    public async Task PushSongAsync(int songId, CancellationToken ct)
    {
        var opts = navOptions.CurrentValue;
        if (!opts.IsConfigured)
            return;

        var ownerId = ownerLookup.OwnerUserId;
        var song = await db.Songs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == songId && s.OwnerUserId == ownerId && s.DeletedAtUtc == null, ct);
        if (song is null)
            return;

        var (sourceDir, destDir) = Dirs();
        var key = BuildKey(
            new MhLikeRow(song.Id, song.SourcePath, song.DestinationPath, song.MusicBrainzId,
                song.Artist, song.Title, song.DurationSeconds, song.IsLiked, song.LikeLastSyncedValue),
            sourceDir, destDir);

        var matches = await ResolveMatchesAsync(key, BuildSearchQuery(song.Artist, song.Title), opts, ct);
        if (matches.Count == 0)
        {
            logger.LogDebug("No Navidrome match to {Action} for song {SongId}", song.IsLiked ? "star" : "unstar", songId);
            return; // the sweep will retry once the track exists in Navidrome
        }

        if (await ApplyStarAsync(matches, star: song.IsLiked, ct))
        {
            song.MarkLikeSynced(song.IsLiked);
            song.NavidromeSongId = matches[0].Id;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<IReadOnlyList<NavidromeSong>> ResolveMatchesAsync(
        LikeMatchKey key, string query, NavidromeOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        var results = await client.SearchSongsAsync(query, opts.SearchLimit, ct);
        var index = new NavidromeSongIndex(results, opts.FuzzyDurationToleranceSeconds);
        return index.Find(key);
    }

    private async Task<bool> ApplyStarAsync(IReadOnlyList<NavidromeSong> matches, bool star, CancellationToken ct)
    {
        var allOk = true;
        foreach (var m in matches)
        {
            try
            {
                if (star) await client.StarAsync(m.Id, ct);
                else await client.UnstarAsync(m.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                allOk = false;
                logger.LogWarning(ex, "Failed to {Action} Navidrome song {NavId}", star ? "star" : "unstar", m.Id);
            }
        }
        return allOk;
    }

    private async Task PersistAsync(
        List<(int Id, bool Liked)> pulls,
        Dictionary<int, bool> newBase,
        Dictionary<int, string> resolvedNavId,
        Guid ownerId,
        CancellationToken ct)
    {
        var pullMap = pulls.ToDictionary(p => p.Id, p => p.Liked);
        var ids = newBase.Keys.Union(pullMap.Keys).Distinct().ToList();
        if (ids.Count == 0)
            return;

        var songs = await db.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && ids.Contains(s.Id))
            .ToListAsync(ct);

        foreach (var song in songs)
        {
            if (pullMap.TryGetValue(song.Id, out var liked))
                song.SetLiked(liked);
            if (newBase.TryGetValue(song.Id, out var b))
                song.MarkLikeSynced(b);
            if (resolvedNavId.TryGetValue(song.Id, out var navId))
                song.NavidromeSongId = navId;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<MhLikeRow>> LoadRowsAsync(Guid ownerId, CancellationToken ct)
    {
        var rows = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null)
            .Select(s => new MhLikeRow(
                s.Id, s.SourcePath, s.DestinationPath, s.MusicBrainzId,
                s.Artist, s.Title, s.DurationSeconds, s.LikedAtUtc != null, s.LikeLastSyncedValue))
            .Take(MaxSongs + 1)
            .ToListAsync(ct);

        if (rows.Count > MaxSongs)
            logger.LogWarning("Navidrome like sweep capped at {Max} songs; some may not sync", MaxSongs);
        return rows;
    }

    private (string SourceDir, string DestDir) Dirs()
    {
        var e = enricherOptions.CurrentValue;
        return (e.SourceDirectory ?? string.Empty, e.DestinationDirectory ?? string.Empty);
    }

    private static LikeMatchKey BuildKey(MhLikeRow row, string sourceDir, string destDir)
        => NavidromeLikeMatcher.BuildKey(
            row.Id, row.SourcePath, row.DestinationPath, row.MusicBrainzId,
            row.Artist, row.Title, row.DurationSeconds, sourceDir, destDir);

    private static string BuildSearchQuery(MhLikeRow row) => BuildSearchQuery(row.Artist, row.Title);

    private static string BuildSearchQuery(string? artist, string? title)
    {
        title = title?.Trim();
        artist = artist?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return artist ?? string.Empty;
        return string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
    }
}

/// <summary>Small outcome record for one reconcile sweep (logging/observability).</summary>
public sealed record ReconcileSummary(int SongsConsidered, int RemoteStarred, int Pulled, int Starred, int Unstarred)
{
    public static ReconcileSummary Empty { get; } = new(0, 0, 0, 0, 0);
    public bool HasWork => Pulled > 0 || Starred > 0 || Unstarred > 0;
}
