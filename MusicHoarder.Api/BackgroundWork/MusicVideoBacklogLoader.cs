using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork;

public sealed record BacklogSet(
    int Id,
    DateTime SearchedAtUtc,
    DateTime? NextSearchAtUtc,
    int EmptySearches,
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
    bool Due);

public sealed record MusicVideoBacklog(DateTime Now, MusicEnricherOptions Options, IReadOnlyList<BacklogSong> Songs);

public sealed class MusicVideoBacklogLoader(
    MusicHoarderDbContext db,
    MusicVideoOutcomeMemo memo,
    IOptionsMonitor<MusicEnricherOptions> options,
    TimeProvider time)
{
    private const int JsonChunk = 400;

    private sealed record SongFacts(
        int Id, string? Artist, string? Title, int? DurationMs, int? DurationSeconds, DateTime AddedAt);

    private sealed record SetFacts(
        int Id, int SongId, string QueryKey, DateTime SearchedAtUtc, DateTime? NextSearchAtUtc,
        int EmptySearches, bool HasError, bool SearchErrored);

    public async Task<MusicVideoBacklog> LoadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var opts = options.CurrentValue;

        var newestFirst = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Eligible(db)
            .InPrefetchOrder()
            .Select(s => new SongFacts(s.Id, s.Artist, s.Title, s.DurationMs, s.DurationSeconds, s.AcquiredAtUtc ?? s.IndexedAtUtc))
            .ToListAsync(ct);

        var sets = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
            .Select(c => new SetFacts(
                c.Id, c.SongId, c.QueryKey, c.SearchedAtUtc, c.NextSearchAtUtc, c.EmptySearches,
                c.Error != null,
                c.Error != null && c.Error != MusicVideoCandidateCache.NoCandidatesError))
            .ToListAsync(ct);
        var setBySong = sets.ToDictionary(c => c.SongId);

        var videoBySong = (await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Select(v => new { v.SongId, v.Status, v.Pick })
            .ToListAsync(ct)).ToDictionary(v => v.SongId);

        var gates = MusicVideoOutcomeMemo.GatesOf(opts);
        var verdicts = new Dictionary<int, MusicVideoVerdict>();
        var unread = new List<(SongFacts Song, SetFacts Set)>();
        foreach (var song in newestFirst)
        {
            if (!setBySong.TryGetValue(song.Id, out var set))
                continue;
            if (set.HasError || set.EmptySearches > 0)
                verdicts[set.Id] = new MusicVideoVerdict(MusicVideoSearchOutcome.Empty, null, null);
            else if (memo.Get(set.Id, set.SearchedAtUtc, gates) is { } known)
                verdicts[set.Id] = known;
            else
                unread.Add((song, set));
        }

        foreach (var chunk in unread.Chunk(JsonChunk))
        {
            var ids = chunk.Select(u => u.Set.Id).ToList();
            var json = await db.SongVideoCandidateSets.IgnoreQueryFilters().AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.CandidatesJson })
                .ToDictionaryAsync(c => c.Id, c => c.CandidatesJson, ct);
            foreach (var (song, set) in chunk)
            {
                var candidates = json.TryGetValue(set.Id, out var text) ? MusicVideoCandidateCache.TryParse(text) ?? [] : [];
                var verdict = VerdictOf(candidates, song, opts);
                memo.Remember(set.Id, set.SearchedAtUtc, gates, verdict);
                verdicts[set.Id] = verdict;
            }
        }
        memo.Retain(sets.Select(c => c.Id).ToHashSet());

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
            var due = inScope && MusicVideoPrefetchScope.IsDue(
                new PrefetchSong(song.Id, song.Artist, song.Title, song.DurationMs, song.DurationSeconds),
                set is null ? null : new PrefetchSet(set.SongId, set.QueryKey, set.NextSearchAtUtc),
                now);

            oldestFirst.Add(new BacklogSong(
                song.Id, song.AddedAt, inScope, video is not null, working,
                set is null ? null : new BacklogSet(set.Id, set.SearchedAtUtc, set.NextSearchAtUtc, set.EmptySearches, set.SearchErrored),
                set is null ? null : verdicts[set.Id],
                outOfDate, due));
        }
        return new MusicVideoBacklog(now, opts, oldestFirst);
    }

    private static MusicVideoVerdict VerdictOf(List<CachedVideoCandidate> candidates, SongFacts song, MusicEnricherOptions opts)
    {
        var outcome = MusicVideoAutoPick.OutcomeOfStoredSet(
            hasError: false, emptySearches: 0, candidates, song.Artist, song.DurationMs, song.DurationSeconds, opts);
        long? pickBytes = outcome == MusicVideoSearchOutcome.Clip
            ? MusicVideoAutoPick.Choose(candidates, song.Artist, song.DurationMs, song.DurationSeconds, opts)?.EstimatedBytes
            : null;
        return new MusicVideoVerdict(outcome, candidates.Count > 0 ? candidates.Max(c => c.Score) : null, pickBytes);
    }
}
