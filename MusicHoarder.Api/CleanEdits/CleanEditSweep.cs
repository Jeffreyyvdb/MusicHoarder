using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>A snapshot of the sweep. <see cref="Processed"/> counts every song looked at, checked or not.</summary>
public sealed record CleanEditSweepProgress(
    bool Running,
    bool Stopping,
    int Total,
    int Processed,
    int Checked,
    int Skipped,
    int Failed,
    string? Current,
    string? StoppedReason,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>
/// The admin "check library" run: walks one account's built songs, newest first, and stores a
/// <see cref="CleanEditCheck"/> for each one that has none for its current file. A re-run does only
/// what is missing, so stopping and starting again resumes. It is independent of the pipeline's
/// <c>JobManager</c> (it only reads files and catalogs, and the catalogs set its pace) and never
/// writes to an audio file or a song row.
/// </summary>
internal sealed class CleanEditSweep(
    IServiceScopeFactory scopes,
    CleanEditChecker checker,
    IHostApplicationLifetime lifetime,
    ILogger<CleanEditSweep> logger)
{
    /// <summary>Catalogs that answer nothing this many times in a row are rate limiting us or down; stop rather than hammer them.</summary>
    internal const int ConsecutiveFailuresBeforeStopping = 5;

    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private CleanEditSweepProgress _progress = new(false, false, 0, 0, 0, 0, 0, null, null, null, null);

    public CleanEditSweepProgress Progress
    {
        get
        {
            lock (_gate)
                return _progress;
        }
    }

    /// <summary>Starts a run for <paramref name="ownerUserId"/>'s library. False when one is already running.</summary>
    public bool TryStart(Guid ownerUserId)
    {
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_progress.Running)
                return false;
            cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _cancel = cancel;
            _progress = new CleanEditSweepProgress(true, false, 0, 0, 0, 0, 0, null, null, DateTime.UtcNow, null);
        }

        _ = Task.Run(() => RunAsync(ownerUserId, cancel));
        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_progress.Running)
                return;
            _progress = _progress with { Stopping = true };
            _cancel?.Cancel();
        }
    }

    private async Task RunAsync(Guid ownerUserId, CancellationTokenSource cancel)
    {
        var ct = cancel.Token;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

            var songs = await db.Songs.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OwnerUserId == ownerUserId && s.DeletedAtUtc == null
                    && s.DestinationPath != null && s.Artist != null && s.Title != null)
                .OrderByDescending(s => s.Id)
                .Select(s => new Candidate(s.Id, s.Artist!, s.Title!, s.Isrc,
                    db.WishlistItems.IgnoreQueryFilters()
                        .Where(w => w.DownloadedSongId == s.Id && w.DurationMs > 0)
                        .Select(w => (int?)w.DurationMs)
                        .FirstOrDefault(),
                    s.DestinationPath!))
                .ToListAsync(ct);
            var checks = await db.CleanEditChecks.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.OwnerUserId == ownerUserId)
                .Select(c => new { c.SongId, c.Verdict, c.FileStamp })
                .ToDictionaryAsync(c => c.SongId, ct);
            Update(p => p with { Total = songs.Count });

            var failedInARow = 0;
            foreach (var song in songs)
            {
                ct.ThrowIfCancellationRequested();
                var stamp = FileStamp(song.Path);
                var current = checks.GetValueOrDefault(song.Id);
                if (stamp is null || (current is not null && current.Verdict != CleanEditVerdict.Failed && current.FileStamp == stamp))
                {
                    Update(p => p with { Processed = p.Processed + 1, Skipped = p.Skipped + 1 });
                    continue;
                }

                Update(p => p with { Current = $"{song.Artist} - {song.Title}" });
                var outcome = await CheckAsync(song, ct);
                await SaveAsync(db, ownerUserId, song.Id, stamp, outcome, ct);

                failedInARow = outcome.Verdict == CleanEditVerdict.Failed ? failedInARow + 1 : 0;
                var failed = outcome.Verdict == CleanEditVerdict.Failed;
                Update(p => p with
                {
                    Processed = p.Processed + 1,
                    Checked = p.Checked + (failed ? 0 : 1),
                    Failed = p.Failed + (failed ? 1 : 0),
                });
                if (failedInARow >= ConsecutiveFailuresBeforeStopping)
                {
                    Finish("Stopped after repeated failures: the catalogs are not answering. Run it again later.");
                    return;
                }
            }
            Finish(null);
        }
        catch (OperationCanceledException)
        {
            Finish(lifetime.ApplicationStopping.IsCancellationRequested ? "The server is shutting down." : "Stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Clean-edit sweep failed");
            Finish("The run failed. See the server log.");
        }
        finally
        {
            cancel.Dispose();
        }
    }

    private async Task<CleanEditOutcome> CheckAsync(Candidate song, CancellationToken ct)
    {
        try
        {
            return await checker.CheckAsync(
                new CleanEditSubject(song.Artist, song.Title, song.Isrc, song.RequestedDurationMs / 1000.0, song.Path), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Clean-edit check failed for song {SongId}", song.Id);
            return CleanEditOutcome.Failed;
        }
    }

    private static async Task SaveAsync(
        MusicHoarderDbContext db, Guid ownerUserId, int songId, string stamp, CleanEditOutcome outcome, CancellationToken ct)
    {
        var row = await db.CleanEditChecks.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.SongId == songId, ct);
        if (row is null)
        {
            row = new CleanEditCheck { SongId = songId, OwnerUserId = ownerUserId };
            db.CleanEditChecks.Add(row);
        }
        row.Verdict = outcome.Verdict;
        row.Margin = outcome.Margin;
        row.EditedSeconds = outcome.EditedSeconds;
        row.EvidenceJson = outcome.Evidence?.ToJson();
        row.FileStamp = stamp;
        row.CheckedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>The file's path and size, or null when it is not there to check.</summary>
    internal static string? FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{path}|{info.Length}" : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Finish(string? reason)
    {
        Update(p => p with { Running = false, Stopping = false, Current = null, StoppedReason = reason, CompletedAtUtc = DateTime.UtcNow });
    }

    private void Update(Func<CleanEditSweepProgress, CleanEditSweepProgress> change)
    {
        lock (_gate)
            _progress = change(_progress);
    }

    private sealed record Candidate(int Id, string Artist, string Title, string? Isrc, int? RequestedDurationMs, string Path);
}
