using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Assets;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// The library build in <see cref="StorageMode.Hosted"/>. A song is built by linking it to the
/// <see cref="AudioAsset"/> of the file it already has and marking it Done: nothing is copied, no tags
/// are written, and the destination tree is never touched. The tags the app shows live in the database.
/// It takes the same candidates as the folder build (<see cref="LibraryBuildQuery.BuildCandidates"/>
/// with <c>hostedStorage</c>), so the pending-work poll agrees with it.
/// </summary>
public sealed class HostedLibraryBuilder(
    IServiceScopeFactory scopeFactory,
    AudioAssetRegistry registry,
    IOptions<MusicEnricherOptions> options,
    Observability.PipelineMetrics metrics,
    ILogger<HostedLibraryBuilder> logger) : ILibraryBuilderService
{
    internal const string MissingFileError = "The audio file is missing.";

    public async Task<LibraryBuildBatchResult> ProcessNextBatchAsync(Guid runId, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var opts = options.Value;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var songs = await LibraryBuildQuery.BuildCandidates(
                db.Songs.IgnoreQueryFilters(),
                LibraryBuildQuery.LyricsWaitCutoff(opts),
                opts.MaxLibraryBuildAttempts,
                opts.EnableBuildNeedsReview,
                db.UpgradeRequests.IgnoreQueryFilters(),
                hostedStorage: true)
            .OrderBy(s => s.Id)
            .Take(opts.LibraryBuilderBatchSize)
            .ToListAsync(ct);
        if (songs.Count == 0)
            return new LibraryBuildBatchResult(0, 0, 0, DateTime.UtcNow - startedAt);

        // Hashing reads the whole file; do it in parallel, off the context.
        var read = new ConcurrentDictionary<int, AudioFileFacts?>();
        await Parallel.ForEachAsync(
            songs.Where(NeedsRead),
            new ParallelOptions { MaxDegreeOfParallelism = opts.LibraryBuilderWorkerConcurrency, CancellationToken = ct },
            async (song, token) => read[song.Id] = await registry.ReadAsync(song.SourcePath, token));

        var done = 0;
        var failed = 0;
        foreach (var song in songs)
        {
            if (await LinkAsync(db, song, read.GetValueOrDefault(song.Id), ct))
            {
                song.MarkBuiltInPlace();
                done++;
            }
            else
            {
                song.MarkBuildFailed(MissingFileError);
                failed++;
            }
        }
        await db.SaveChangesAsync(ct);

        var duration = DateTime.UtcNow - startedAt;
        logger.LogInformation(
            "Hosted library build run {RunId} complete: Total={Total}, Done={Done}, Failed={Failed}, Duration={Duration:F1}s",
            runId, songs.Count, done, failed, duration.TotalSeconds);
        metrics.RecordStageDuration("build", duration.TotalSeconds);
        metrics.RecordTerminal("built", done);
        metrics.RecordTerminal("build_failed", failed);
        return new LibraryBuildBatchResult(songs.Count, done, failed, duration);
    }

    /// <summary>
    /// A row whose source was released has only its destination copy, and that copy is tag-written, so
    /// it is never read here. A content-store file is named by its hash and needs no read either.
    /// </summary>
    private bool NeedsRead(SongMetadata song)
        => song.AudioAssetId is null && !song.IsSourceReleased && registry.ContentStoreSha(song.SourcePath) is null;

    /// <summary>
    /// Gives the song an asset when its file is there to register. True when the song has a file to play:
    /// its asset's, or, for a row an earlier folder-mode build copied, that destination copy.
    /// </summary>
    private async Task<bool> LinkAsync(MusicHoarderDbContext db, SongMetadata song, AudioFileFacts? facts, CancellationToken ct)
    {
        if (song.AudioAssetId is not null)
            return File.Exists(song.SourcePath) || HasDestinationCopy(song);

        if (!song.IsSourceReleased && registry.ContentStoreSha(song.SourcePath) is { } sha && File.Exists(song.SourcePath))
        {
            if (await AudioAssetRegistry.FindAsync(db, sha, ct) is { } stored)
            {
                registry.Link(song, stored);
                return true;
            }
            facts = await registry.ReadAsync(song.SourcePath, ct);
        }

        if (facts is not null)
        {
            await registry.LinkAsync(db, song, facts, ct);
            return true;
        }

        return HasDestinationCopy(song);
    }

    private static bool HasDestinationCopy(SongMetadata song)
        => !string.IsNullOrEmpty(song.DestinationPath) && File.Exists(song.DestinationPath);
}
