using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>A candidate as remembered: the search's row plus whatever a probe has measured about it.</summary>
public record CachedVideoCandidate(
    string VideoId,
    string Title,
    string Channel,
    int? DurationSeconds,
    int Score,
    bool HasThumbnail,
    MusicVideoMotion Motion,
    long? EstimatedBytes,
    bool SquareSource);

/// <summary>A song's candidates and when they were searched.</summary>
public record VideoCandidateList(IReadOnlyList<CachedVideoCandidate> Candidates, DateTime SearchedAtUtc);

/// <summary>
/// Remembers each song's music-video candidates in <see cref="SongVideoCandidateSet"/>, so the picker
/// opens on a list rather than a YouTube search. One owner of the rule for when a remembered list may
/// be served (<see cref="GetAsync"/>) and of how a list is searched and stored
/// (<see cref="SearchAsync"/>), shared by the candidates endpoint and the background prefetch.
/// </summary>
public class MusicVideoCandidateCache(
    IMusicVideoDownloader downloader,
    IMusicVideoProbe probe,
    IOptions<MusicEnricherOptions> options,
    TimeProvider time,
    ILogger<MusicVideoCandidateCache> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What a search that found nothing is recorded as; see <see cref="SearchAsync"/>.</summary>
    public const string NoCandidatesError = "the search found no candidates";

    /// <summary>
    /// How candidates are ranked. A stored list carries the scores and the order of the ranking that
    /// searched it, so a change to <see cref="MusicVideoDownloader.ScoreCandidate"/> that should reach
    /// stored lists bumps this: every list then fails <see cref="IsServable"/> and is searched again
    /// (the picker on open, the prefetch for recent songs), keeping the probes already measured.
    /// 2: a candidate must name one of the song's artists, and title tokens match whole words
    ///    including 1- and 2-character tokens (lists from before carry no version).
    /// </summary>
    internal const int RankingVersion = 2;

    /// <summary>
    /// The search a list answers: what <see cref="IMusicVideoDownloader.SuggestAsync"/> is given about
    /// the song, and the <see cref="RankingVersion"/> that ranked it. Re-enrichment that renames a song
    /// changes it, and the old list is then not served.
    /// </summary>
    public static string QueryKeyFor(SongMetadata song) =>
        QueryKeyFor(song.Artist, song.Title, song.DurationMs, song.DurationSeconds);

    /// <summary>The same key from just the fields it reads, for a pass over the whole library that loads no more.</summary>
    internal static string QueryKeyFor(string? artist, string? title, int? durationMs, int? durationSeconds)
    {
        var text = $"{artist}\n{title}\n{durationMs ?? durationSeconds * 1000}\nranking {RankingVersion}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// The remembered list for <paramref name="song"/>, or null when there is none worth serving: never
    /// searched, last search failed, searched for a different title, or older than
    /// <c>MusicVideoCandidateCacheDays</c>.
    /// </summary>
    public async Task<VideoCandidateList?> GetAsync(
        MusicHoarderDbContext db, SongMetadata song, CancellationToken ct)
    {
        var row = await SetOf(db, song.Id).AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null || !IsServable(row, QueryKeyFor(song)))
            return null;
        return new VideoCandidateList(Read(row), row.SearchedAtUtc);
    }

    /// <summary>How long a failed search waits before the prefetch tries that song again.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

    /// <summary>Whether the prefetch should search again: the last search failed long enough ago, or its list is not servable.</summary>
    public bool NeedsSearch(SongVideoCandidateSet set, string queryKey, DateTime now)
        => set.Error is not null
            ? now - set.SearchedAtUtc >= RetryFailedAfter
            : !IsServable(set, queryKey);

    /// <summary>Whether a stored set still answers the song's current search.</summary>
    public bool IsServable(SongVideoCandidateSet row, string queryKey) =>
        row.Error is null
        && row.QueryKey == queryKey
        && time.GetUtcNow().UtcDateTime - row.SearchedAtUtc
            < TimeSpan.FromDays(options.Value.MusicVideoCandidateCacheDays);

    /// <summary>
    /// Searches YouTube for the song's candidates and remembers the list. Measurements already taken
    /// of a video carry over (a video's picture does not change). With <paramref name="probeTop"/>, the
    /// leading <c>MusicVideoProbeCandidates</c> still unmeasured are probed first, one at a time —
    /// the background prefetch's job; the endpoint leaves probing to its per-candidate requests.
    /// An empty search is recorded as a failure, not remembered. Search errors propagate; a failure
    /// to store does not.
    /// </summary>
    public async Task<VideoCandidateList> SearchAsync(
        MusicHoarderDbContext db, SongMetadata song, string? currentVideoId, bool probeTop, CancellationToken ct)
    {
        var found = await downloader.SuggestAsync(
            new MusicVideoFetchRequest(
                currentVideoId, PinIsExplicit: false,
                song.Artist ?? string.Empty, song.Title ?? string.Empty,
                song.DurationMs ?? song.DurationSeconds * 1000),
            ct);

        var row = await SetOf(db, song.Id).FirstOrDefaultAsync(ct);

        // Nothing but the pinned current video (which the search adds itself) means the search
        // found nothing — and an empty answer is also what a refused search looks like (a bot
        // challenge, a rate limit: yt-dlp prints nothing and the search reads no results). Never
        // remember it as "no candidates", or the picker would show that for weeks instead of
        // searching; and never let it hide a good list remembered earlier.
        if (found.All(c => c.VideoId == currentVideoId))
        {
            if (row is null || !IsServable(row, QueryKeyFor(song)))
                await RecordFailureAsync(db, song, NoCandidatesError, ct);
            else
            {
                // The good list stays; the schedule still learns that the search came back empty.
                Schedule(row, song, MusicVideoSearchOutcome.Empty, time.GetUtcNow().UtcDateTime);
                await TrySaveAsync(db, song.Id, ct);
            }
            return new VideoCandidateList([], time.GetUtcNow().UtcDateTime);
        }

        var measured = row is null
            ? new Dictionary<string, CachedVideoCandidate>()
            : Read(row).Where(c => c.Motion != MusicVideoMotion.Unknown)
                .GroupBy(c => c.VideoId).ToDictionary(g => g.Key, g => g.First());

        var candidates = found.Select(c =>
        {
            var fresh = new CachedVideoCandidate(
                c.VideoId, c.Title, c.Channel, c.DurationSeconds, c.Score,
                HasThumbnail: c.ThumbnailUrl is not null,
                MusicVideoMotion.Unknown, EstimatedBytes: null, SquareSource: false);
            return measured.TryGetValue(c.VideoId, out var old)
                ? fresh with { Motion = old.Motion, EstimatedBytes = old.EstimatedBytes, SquareSource = old.SquareSource }
                : fresh;
        }).ToList();

        if (probeTop)
        {
            var limit = options.Value.MusicVideoProbeCandidates;
            for (var i = 0; i < Math.Min(limit, candidates.Count); i++)
            {
                if (candidates[i].Motion != MusicVideoMotion.Unknown)
                    continue;
                candidates[i] = Merge(candidates[i], await probe.ProbeAsync(candidates[i].VideoId, ct));
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        var stored = row ?? new SongVideoCandidateSet { SongId = song.Id };
        stored.QueryKey = QueryKeyFor(song);
        stored.SearchedAtUtc = now;
        stored.CandidatesJson = JsonSerializer.Serialize(candidates, Json);
        stored.Error = null;
        Schedule(stored, song, MusicVideoAutoPick.Choose(candidates, song, options.Value) is null
            ? MusicVideoSearchOutcome.NoClip
            : MusicVideoSearchOutcome.Clip, now);
        await SaveAsync(db, stored, isNew: row is null, ct);

        return new VideoCandidateList(candidates, now);
    }

    /// <summary>Records that searching for the song failed, so the prefetch waits before it tries again.</summary>
    public async Task RecordFailureAsync(MusicHoarderDbContext db, SongMetadata song, string error, CancellationToken ct)
    {
        var row = await SetOf(db, song.Id).FirstOrDefaultAsync(ct);
        var stored = row ?? new SongVideoCandidateSet { SongId = song.Id, CandidatesJson = "[]" };
        stored.QueryKey = QueryKeyFor(song);
        stored.SearchedAtUtc = time.GetUtcNow().UtcDateTime;
        stored.Error = error.Length > 1024 ? error[..1024] : error;
        Schedule(stored, song, MusicVideoSearchOutcome.Empty, stored.SearchedAtUtc);
        await SaveAsync(db, stored, isNew: row is null, ct);
    }

    /// <summary>
    /// Writes a probe's verdict into the remembered list, so the next open shows it measured. A probe
    /// that measured nothing changes nothing: the candidate stays re-checkable.
    /// </summary>
    public async Task RecordProbeAsync(
        MusicHoarderDbContext db, int songId, MusicVideoProbeResult result, CancellationToken ct)
    {
        if (result.Motion == MusicVideoMotion.Unknown)
            return;

        var row = await SetOf(db, songId).FirstOrDefaultAsync(ct);
        if (row is null)
            return;

        var candidates = Read(row).ToList();
        var index = candidates.FindIndex(c => c.VideoId == result.VideoId);
        if (index < 0)
            return;
        candidates[index] = Merge(candidates[index], result);
        row.CandidatesJson = JsonSerializer.Serialize(candidates, Json);
        await TrySaveAsync(db, songId, ct);
    }

    /// <summary>Sets when the song is searched again, and the run of empty searches that decides it.</summary>
    private static void Schedule(SongVideoCandidateSet row, SongMetadata song, MusicVideoSearchOutcome outcome, DateTime now)
    {
        row.EmptySearches = outcome == MusicVideoSearchOutcome.Empty ? row.EmptySearches + 1 : 0;
        row.NextSearchAtUtc = MusicVideoSearchSchedule.NextSearchAt(
            outcome,
            MusicVideoSearchSchedule.IsRecentRelease(song.ReleaseDate, song.Year, song.AcquiredAtUtc, now),
            row.EmptySearches,
            now);
    }

    private static IQueryable<SongVideoCandidateSet> SetOf(MusicHoarderDbContext db, int songId) =>
        db.SongVideoCandidateSets.IgnoreQueryFilters().Where(c => c.SongId == songId);

    private static CachedVideoCandidate Merge(CachedVideoCandidate candidate, MusicVideoProbeResult probed) =>
        probed.Motion == MusicVideoMotion.Unknown && probed.Error is not null
            ? candidate // the probe failed: leave the row unmeasured and re-checkable
            : candidate with
            {
                Motion = probed.Motion,
                EstimatedBytes = probed.EstimatedBytes,
                SquareSource = probed.SquareSource,
                // A pinned row the search never described gets its words from the probe.
                Title = string.IsNullOrEmpty(candidate.Title) ? probed.Title : candidate.Title,
                Channel = string.IsNullOrEmpty(candidate.Channel) ? probed.Channel : candidate.Channel,
                DurationSeconds = candidate.DurationSeconds ?? probed.DurationSeconds,
                HasThumbnail = candidate.HasThumbnail || probed.ThumbnailUrl is not null,
            };

    private IReadOnlyList<CachedVideoCandidate> Read(SongVideoCandidateSet row)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CachedVideoCandidate>>(row.CandidatesJson, Json) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unreadable music video candidate list for song {SongId}", row.SongId);
            return [];
        }
    }

    /// <summary>
    /// Stores <paramref name="row"/>. A remembered list is a convenience: losing a race to another
    /// writer (the prefetch and the picker searching the same song at once) leaves the other's
    /// equivalent list, and must not fail anything.
    ///
    /// <para>
    /// A song's first set is inserted with <c>ON CONFLICT DO NOTHING</c>: both writers can have found
    /// no row, and a plain insert would make the loser's save fail on the one-set-per-song index —
    /// an error in the database's log and EF's for an outcome that is fine. The loser yields to the
    /// row already there, as a lost update does.
    /// </para>
    /// </summary>
    private async Task SaveAsync(MusicHoarderDbContext db, SongVideoCandidateSet row, bool isNew, CancellationToken ct)
    {
        if (!isNew || !db.Database.IsNpgsql())
        {
            if (isNew)
                db.SongVideoCandidateSets.Add(row);
            await TrySaveAsync(db, row.SongId, ct);
            return;
        }

        try
        {
            var inserted = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SongVideoCandidateSets"
                    ("SongId", "QueryKey", "SearchedAtUtc", "CandidatesJson", "Error", "NextSearchAtUtc", "EmptySearches")
                VALUES ({row.SongId}, {row.QueryKey}, {row.SearchedAtUtc}, {row.CandidatesJson}, {row.Error},
                    {row.NextSearchAtUtc}, {row.EmptySearches})
                ON CONFLICT ("SongId") DO NOTHING
                """, ct);
            if (inserted == 0)
                logger.LogDebug("Music video candidates for song {SongId} were stored by another writer first", row.SongId);
        }
        catch (DbException ex)
        {
            // The song was deleted meanwhile (the foreign key), or the database is unreachable.
            logger.LogInformation(ex, "Music video candidates for song {SongId} were not stored", row.SongId);
        }
    }

    private async Task TrySaveAsync(MusicHoarderDbContext db, int songId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Music video candidates for song {SongId} were not stored", songId);
            db.ChangeTracker.Clear();
        }
    }
}
