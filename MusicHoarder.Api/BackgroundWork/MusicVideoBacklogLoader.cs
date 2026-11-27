using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.BackgroundWork;

public sealed record BacklogSet(
    int Id,
    DateTime SearchedAtUtc,
    DateTime? NextSearchAtUtc,
    int EmptySearches,
    bool HasError,
    bool SearchErrored);

public sealed record BacklogSong(
    int Id,
    DateTime AddedAt,
    bool InScope,
    bool HasVideoRow,
    MusicVideoPick? WorkingVideoPick,
    BacklogSet? Set,
    MusicVideoVerdict? Verdict,
    bool OutOfDate,
    bool Due,
    int Priority);

public sealed record MusicVideoBacklog(DateTime Now, MusicEnricherOptions Options, IReadOnlyList<BacklogSong> Songs);

public sealed class MusicVideoBacklogLoader(
    MusicHoarderDbContext db,
    MusicVideoOutcomeMemo memo,
    IOptionsMonitor<MusicEnricherOptions> options,
    IOptionsMonitor<SyncOptions> syncOptions,
    TimeProvider time)
{
    private const int JsonChunk = 400;

    private sealed record SongFacts(
        int Id,
        Guid OwnerUserId,
        string? Artist,
        string? Title,
        int? DurationMs,
        int? DurationSeconds,
        DateTime AddedAt,
        string SourcePath,
        string? SpotifyId,
        DateTime? LikedAtUtc,
        SongAcquisitionIntent AcquisitionIntent,
        LibraryBuildStatus BuildStatus,
        string? DestinationPath,
        EnrichmentStatus EnrichmentStatus);

    private sealed record SetFacts(
        int Id, int SongId, string QueryKey, DateTime SearchedAtUtc, DateTime? NextSearchAtUtc,
        int EmptySearches, bool HasError, bool SearchErrored);

    public async Task<MusicVideoBacklog> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.CurrentValue;
        var syncedDir = syncOptions.CurrentValue.SyncedSourceDirectory;

        var newestFirst = await MusicVideoPrefetchScope.Eligible(db)
            .InPrefetchOrder()
            .Select(s => new SongFacts(
                s.Id, s.OwnerUserId, s.Artist, s.Title, s.DurationMs, s.DurationSeconds,
                s.AcquiredAtUtc ?? s.IndexedAtUtc,
                s.SourcePath, s.SpotifyId, s.LikedAtUtc, s.AcquisitionIntent,
                s.LibraryBuildStatus, s.DestinationPath, s.EnrichmentStatus))
            .ToListAsync(ct);

        var saveDates = await SpotifySaveDates.LoadAsync(db, ct, allTenants: true);

        var sets = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new SetFacts(
                c.Id, c.SongId, c.QueryKey, c.SearchedAtUtc, c.NextSearchAtUtc, c.EmptySearches,
                c.Error != null,
                c.Error != null && c.Error != MusicVideoCandidateCache.NoCandidatesError))
            .ToListAsync(ct);
        var setBySong = sets.ToDictionary(c => c.SongId);

        var videoBySong = (await MusicVideoPrefetchScope.VideosOfEligible(db)
            .Select(v => new { v.SongId, v.Status, v.Pick })
            .ToListAsync(ct)).ToDictionary(v => v.SongId);

        var gates = MusicVideoPickGates.From(opts);
        var verdicts = new Dictionary<int, MusicVideoVerdict>();
        var unread = new List<(SongFacts Song, SetFacts Set, MusicVideoVerdictKey Key, int Rewrites)>();
        foreach (var song in newestFirst)
        {
            if (!setBySong.TryGetValue(song.Id, out var set))
                continue;
            var key = new MusicVideoVerdictKey(set.SearchedAtUtc, gates, song.Artist, song.DurationMs, song.DurationSeconds);
            if (set.HasError || set.EmptySearches > 0)
                verdicts[set.Id] = new MusicVideoVerdict(MusicVideoSearchOutcome.Empty, null, null);
            else if (memo.Get(song.Id, key) is { } known)
                verdicts[set.Id] = known;
            else
                unread.Add((song, set, key, memo.RewritesOf(song.Id)));
        }

        foreach (var chunk in unread.Chunk(JsonChunk))
        {
            var ids = chunk.Select(u => u.Set.Id).ToList();
            var json = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.CandidatesJson })
                .ToDictionaryAsync(c => c.Id, c => c.CandidatesJson, ct);
            foreach (var (song, set, key, rewrites) in chunk)
            {
                var candidates = json.TryGetValue(set.Id, out var text) ? MusicVideoCandidateCache.TryParse(text) ?? [] : [];
                var (_, verdict) = MusicVideoAutoPick.Evaluate(
                    candidates, song.Artist, song.DurationMs, song.DurationSeconds, gates);
                memo.Remember(song.Id, rewrites, key, verdict);
                verdicts[set.Id] = verdict;
            }
        }
        memo.Retain(sets.Select(c => c.SongId).ToHashSet());

        var oldestFirst = new List<BacklogSong>(newestFirst.Count);
        for (var i = newestFirst.Count - 1; i >= 0; i--)
        {
            var song = newestFirst[i];
            setBySong.TryGetValue(song.Id, out var set);
            videoBySong.TryGetValue(song.Id, out var video);
            MusicVideoPick? working = video is { Status: not MusicVideoStatus.Failed } ? video.Pick : null;
            var inScope = working is null;
            var outOfDate = set is not null
                && set.QueryKey != MusicVideoCandidateCache.QueryKeyFor(song.Artist, song.Title, song.DurationMs, song.DurationSeconds);
            var priority = MusicVideoPrefetchScope.PriorityOf(
                song.OwnerUserId, song.Id, song.SourcePath, song.SpotifyId, song.LikedAtUtc, song.AcquisitionIntent,
                song.BuildStatus, song.DestinationPath, song.EnrichmentStatus,
                saveDates, opts.DownloadDirectory, syncedDir);
            var due = inScope && MusicVideoPrefetchScope.IsDue(
                new PrefetchSong(song.Id, song.Artist, song.Title, song.DurationMs, song.DurationSeconds, priority, song.AddedAt),
                set is null ? null : new PrefetchSet(set.SongId, set.QueryKey, set.NextSearchAtUtc, set.SearchedAtUtc, set.HasError, set.EmptySearches),
                now);

            oldestFirst.Add(new BacklogSong(
                song.Id, song.AddedAt, inScope, video is not null, working,
                set is null ? null : new BacklogSet(set.Id, set.SearchedAtUtc, set.NextSearchAtUtc, set.EmptySearches, set.HasError, set.SearchErrored),
                set is null ? null : verdicts[set.Id],
                outOfDate, due, priority));
        }
        return new MusicVideoBacklog(now, opts, oldestFirst);
    }
}
