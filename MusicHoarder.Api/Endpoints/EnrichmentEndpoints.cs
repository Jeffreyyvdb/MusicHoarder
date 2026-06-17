using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Endpoints;

public static class EnrichmentEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IEndpointRouteBuilder MapEnrichmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/enrichment").WithTags("Enrichment");

        group.MapPost("/scan", (JobManager jobManager, IDirectoryAvailability availability) =>
            {
                if (!availability.Current.SourceAvailable)
                    return Results.Conflict(new { message = "Source directory is offline. Reconnect to your music library before scanning." });

                if (!jobManager.TryStartJob(JobType.Scan, out var jobId, out _))
                    return Results.Conflict(new { message = "A job is already running. Use POST /api/enrichment/cancel to stop it first." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerEnrichmentScan")
            .WithSummary("Trigger the ScannerService to index the source library.")
            .RequireOwner();

        group.MapPost("/enrich", async (
                EnrichmentPipelineChannel channel,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                // Enrichment is channel-fed by always-running workers (no discrete "enrich job"), so
                // this trigger enqueues every song that's ready for enrichment plus any whose
                // provider-attempt cooldown has elapsed. Works whether or not AutoStartPipeline is on.
                var enqueued = await EnqueueReadyAndRetryableAsync(db, channel, "Manual enrich — all tracks", ct);
                return Results.Accepted("/api/enrichment/status", new { enqueued });
            })
            .WithName("TriggerEnrich")
            .WithSummary("Enqueue all pending/retryable tracks for enrichment via the always-running enrichment workers.")
            .RequireOwner();

        group.MapPost("/enrich/song/{id:int}", async (
                int id,
                bool? reset,
                MusicHoarderDbContext db,
                IEnrichmentOrchestrator orchestrator,
                JobManager jobManager,
                CancellationToken ct) =>
            {
                var song = await db.Songs
                    .Include(s => s.ProviderAttempts)
                    .FirstOrDefaultAsync(s => s.Id == id, ct);
                if (song is null || song.IsDeleted)
                    return Results.NotFound(new { message = $"Song with id {id} not found." });

                var doReset = reset == true;
                if (doReset)
                {
                    song.ResetEnrichment(restoreOriginal: true);
                    song.ResetLibraryBuild();
                    await db.SaveChangesAsync(ct);
                }

                // Run synchronously so the caller gets the exact outcome for this one song —
                // ideal for targeted testing.
                var outcome = await orchestrator.ProcessSongAsync(id, ct);

                // Chain a build so a manual single-song enrich lands in the library even when
                // AutoStartPipeline is off (mirrors the cycle-completion trigger of the channel-fed
                // /enrich and /enrich/folder paths). No-op if a build is already running.
                if (outcome == EnrichmentOutcome.Matched)
                    jobManager.TryStartJob(JobType.Build, out _, out _);

                return Results.Ok(new { songId = id, reset = doReset, outcome = outcome.ToString() });
            })
            .WithName("EnrichSong")
            .WithSummary("Enrich a single song by id and return the outcome. Pass reset=true to clear prior attempts first.")
            .RequireOwner();

        group.MapPost("/enrich/folder", async (
                string path,
                bool? reset,
                EnrichmentPipelineChannel channel,
                MusicHoarderDbContext db,
                IOptions<MusicEnricherOptions> options,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(path))
                    return Results.BadRequest(new { message = "A folder path is required." });

                var doReset = reset == true;
                var prefix = ResolveFolderPrefix(options.Value.SourceDirectory, path);
                var query = db.Songs
                    .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsManuallyApproved)
                    .Where(s => s.SourcePath.StartsWith(prefix + "/"));

                List<int> ids;
                if (doReset)
                {
                    var songs = await query.Include(s => s.ProviderAttempts).ToListAsync(ct);
                    foreach (var song in songs)
                    {
                        song.ResetEnrichment(restoreOriginal: true);
                        song.ResetLibraryBuild();
                    }
                    await db.SaveChangesAsync(ct);
                    ids = songs.Select(s => s.Id).ToList();
                }
                else
                {
                    ids = await query.Select(s => s.Id).ToListAsync(ct);
                }

                channel.EnqueueRange(ids, label: $"Manual enrich — {FolderDisplayName(path)}");
                return Results.Accepted("/api/enrichment/status", new { folder = path, enqueued = ids.Count, reset = doReset });
            })
            .WithName("EnrichFolder")
            .WithSummary("Enqueue every song under a source folder (recursively) for enrichment. Pass reset=true to clear prior attempts first.")
            .RequireOwner();

        group.MapPost("/fingerprint", (JobManager jobManager, IDirectoryAvailability availability) =>
            {
                if (!availability.Current.SourceAvailable)
                    return Results.Conflict(new { message = "Source directory is offline. Reconnect to your music library before fingerprinting." });

                if (!jobManager.TryStartJob(JobType.Fingerprint, out var jobId, out _))
                    return Results.Conflict(new { message = "Fingerprint step is already running." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerFingerprint")
            .WithSummary("Trigger the FingerprintService to fingerprint tracks with missing fingerprints.")
            .RequireOwner();

        group.MapPost("/build", (JobManager jobManager, IDirectoryAvailability availability) =>
            {
                if (!availability.Current.AllAvailable)
                    return Results.Conflict(new { message = "Source/destination directory is offline. Reconnect to your music library before building." });

                if (!jobManager.TryStartJob(JobType.Build, out var jobId, out _))
                    return Results.Conflict(new { message = "Build step is already running." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerBuild")
            .WithSummary("Trigger the LibraryBuilderService to copy and tag matched tracks to the destination.")
            .RequireOwner();

        group.MapPost("/rebuild/album", async (
                string artist,
                string album,
                MusicHoarderDbContext db,
                JobManager jobManager,
                IDirectoryAvailability availability,
                ICanonicalAlbumConsolidator consolidator,
                IOptions<MusicEnricherOptions> options,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
                    return Results.BadRequest(new { message = "artist and album are required." });

                if (!availability.Current.AllAvailable)
                    return Results.Conflict(new { message = "Source/destination directory is offline. Reconnect to your music library before rebuilding." });

                // First choice: consolidate against the multi-provider canonical tracklist — this heals
                // albums whose tracks were each enriched against a different release (split across year
                // folders, duplicate track numbers) by rewriting album title/year + track/disc number
                // and re-queuing the matched tracks. Falls through to a plain re-tag when there's no
                // canonical album for this group.
                if (options.Value.EnableCanonicalDrivenBuild)
                {
                    var result = await consolidator.ConsolidateAsync(db, artist, album, ct);
                    if (result.CanonicalFound)
                    {
                        await db.SaveChangesAsync(ct);

                        // Wake the builder. If a build is already running it'll pick these up on its next
                        // batch, so a 409 here isn't an error — the re-queue still stands.
                        jobManager.TryStartJob(JobType.Build, out var consolidatedJobId, out _);

                        return Results.Accepted("/api/enrichment/status", new
                        {
                            artist,
                            album,
                            consolidated = true,
                            matched = result.Matched,
                            corrected = result.Corrected,
                            requeued = result.Requeued,
                            unmatched = result.Unmatched,
                            jobId = consolidatedJobId,
                        });
                    }
                }

                // Plain re-tag: re-queue this album's already-built tracks so the next build re-copies
                // and re-tags them in place with the current tag-writing logic (e.g. album-identity
                // reconciliation). Enrichment is untouched. Match by logical-album key rather than the
                // exact enriched strings: a split album's halves carry *different* Album/Year values,
                // so an exact match on the clicked half would silently miss the others — the
                // normalized key catches them all in one call. The per-user query filter scopes this
                // to the caller's library.
                var artistKey = AlbumGroupKey.ComputeArtistKey(artist);
                var albumKey = AlbumGroupKey.ComputeAlbumKey(album);
                var builtSongs = await db.Songs
                    .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
                    .Where(s => s.LibraryBuildStatus == LibraryBuildStatus.Done)
                    .Where(s => s.Album != null)
                    .ToListAsync(ct);
                var songs = builtSongs
                    .Where(s => AlbumGroupKey.For(s) is { } key
                        && key.ArtistKey == artistKey
                        && key.AlbumKey == albumKey)
                    .ToList();

                foreach (var song in songs) song.RequeueForRetag();
                await db.SaveChangesAsync(ct);

                // Wake the builder. If a build is already running it'll pick these up on its next batch,
                // so a 409 here isn't an error — the re-queue still stands.
                jobManager.TryStartJob(JobType.Build, out var jobId, out _);

                return Results.Accepted("/api/enrichment/status", new { artist, album, requeued = songs.Count, jobId });
            })
            .WithName("RebuildAlbum")
            .WithSummary("Consolidate an album against its canonical tracklist (fix split year-folders / duplicate track numbers) and re-queue it for re-tag; falls back to a plain in-place re-tag when no canonical album exists.")
            .RequireOwner();

        group.MapGet("/split-albums", async (IAlbumSplitHealer healer, CancellationToken ct) =>
            {
                var splitGroups = await healer.DetectAsync(ct);
                return Results.Ok(new { count = splitGroups.Count, groups = splitGroups });
            })
            .WithName("ListSplitAlbums")
            .WithSummary("Dry-run report of split albums: logical albums whose tracks disagree on identity (release id / album / year / album artist), with the identity a self-heal pass would elect. Empty when the self-heal safeguard has converged everything.")
            .RequireOwner();

        group.MapGet("/missing-artist-credits", async (IArtistCreditHealer healer, CancellationToken ct) =>
            {
                var gaps = await healer.DetectAsync(ct);
                return Results.Ok(new { count = gaps.Count, songs = gaps });
            })
            .WithName("ListMissingArtistCredits")
            .WithSummary("Dry-run report of matched songs missing their discrete artist credit (Artists), with the credit the artist-credit self-heal would backfill from the stored MusicBrainz/Spotify attempt. Empty when the self-heal has converged everything.")
            .RequireOwner();

        group.MapPost("/cancel", (JobManager jobManager, EnrichmentPipelineChannel channel) =>
            {
                var cancelled = jobManager.Cancel();
                // Enrichment workers aren't driven by the job CTS — drain the channel and end the
                // cycle explicitly so the Enrich step doesn't stay stuck Running.
                channel.ResetCycle(cancelled: true);

                if (!cancelled)
                    return Results.Ok(new { message = "No job is currently running." });

                return Results.Ok(new { message = "Cancellation requested for the running job." });
            })
            .WithName("CancelJob")
            .WithSummary("Cancel the currently running job.")
            .RequireOwner();

        group.MapPost("/pause", (string step, JobManager jobManager) =>
            {
                if (!ProgressSnapshotFactory.TryParseJobType(step, out var jobType))
                    return Results.BadRequest(new { message = $"Invalid step '{step}'. Use scan, fingerprint, enrich, or build." });

                jobManager.PauseStep(jobType);
                return Results.Ok(new { message = $"{step} paused.", step, paused = true });
            })
            .WithName("PauseStep")
            .WithSummary("Pause a pipeline step. Cancels any in-flight job for that step and prevents auto-triggering.")
            .RequireOwner();

        group.MapPost("/resume", (string step, JobManager jobManager) =>
            {
                if (!ProgressSnapshotFactory.TryParseJobType(step, out var jobType))
                    return Results.BadRequest(new { message = $"Invalid step '{step}'. Use scan, fingerprint, enrich, or build." });

                jobManager.ResumeStep(jobType);
                return Results.Ok(new { message = $"{step} resumed.", step, paused = false });
            })
            .WithName("ResumeStep")
            .WithSummary("Resume a paused pipeline step so it can auto-trigger again.")
            .RequireOwner();

        group.MapGet("/status", (
                JobManager jobManager,
                ScanProgressTracker scanTracker,
                FingerprintProgressTracker fingerprintTracker,
                EnrichmentProgressTracker enrichmentTracker,
                LibraryBuilderProgressTracker buildTracker,
                DownloadProgressTracker downloadTracker) =>
            {
                var snapshot = ProgressSnapshotFactory.Create(
                    jobManager, scanTracker, fingerprintTracker, enrichmentTracker, buildTracker, downloadTracker);
                return Results.Ok(new { Progress = snapshot });
            })
            .WithName("GetEnrichmentStatus")
            .WithSummary("Get the current job status and a progress snapshot.");

        group.MapGet("/library-availability", (IDirectoryAvailability availability) =>
                Results.Ok(availability.Current))
            .WithName("GetLibraryAvailability")
            .WithSummary("Whether the configured source/destination directories are currently reachable.");

        group.MapGet("/progress", StreamProgress)
            .WithName("StreamEnrichmentProgress")
            .WithSummary("SSE stream that emits a ProgressSnapshot every second while a job is running.");

        group.MapPost("/scrape-trackers", () =>
                Results.Json(new { message = "Tracker scraping is not yet implemented." }, statusCode: 501))
            .WithName("ScrapeTrackers")
            .WithSummary("Trigger on-demand tracker scraping (not yet implemented).");

        group.MapPost("/purge-post-fingerprint", (
                JobManager jobManager,
                IServiceScopeFactory scopeFactory,
                PurgeStatusTracker tracker,
                ICurrentUserAccessor currentUser,
                ILoggerFactory loggerFactory) =>
                StartPurge(
                    mode: "post-fingerprint",
                    jobManager,
                    scopeFactory,
                    tracker,
                    currentUser.UserId,
                    loggerFactory,
                    (service, jobId, ownerUserId, ct) => service.ResetPostFingerprintAsync(jobId, ownerUserId, ct)))
            .WithName("PurgePostFingerprint")
            .WithSummary("Start a background reset of enrichment, lyrics, duplicate, and library-build state. Returns 202 Accepted with a jobId; poll /purge-status for progress.")
            .RequireOwner();

        group.MapPost("/purge-all", (
                JobManager jobManager,
                IServiceScopeFactory scopeFactory,
                PurgeStatusTracker tracker,
                ICurrentUserAccessor currentUser,
                ILoggerFactory loggerFactory) =>
                StartPurge(
                    mode: "all",
                    jobManager,
                    scopeFactory,
                    tracker,
                    currentUser.UserId,
                    loggerFactory,
                    (service, jobId, ownerUserId, ct) => service.PurgeAllAsync(jobId, ownerUserId, ct)))
            .WithName("PurgeAll")
            .WithSummary("Start a background hard-delete of every song, provider attempt, cached Spotify match, and copied destination file. Returns 202 Accepted.")
            .RequireOwner();

        group.MapGet("/purge-status", (PurgeStatusTracker tracker) => Results.Ok(tracker.Get()))
            .WithName("GetPurgeStatus")
            .WithSummary("Get the current purge status snapshot. Includes the last completed result until a new purge starts.");

        return app;
    }

    private static async Task<int> EnqueueReadyAndRetryableAsync(
        MusicHoarderDbContext db, EnrichmentPipelineChannel channel, string? label, CancellationToken ct)
    {
        var pendingIds = await db.Songs
            .AsNoTracking()
            .Where(s => !s.IsSynthetic)
            .WhereReadyForEnrichment()
            .Select(s => s.Id)
            .ToListAsync(ct);

        var retryIds = await db.SongProviderAttempts
            .AsNoTracking()
            .WhereRetryableProviderAttempts(DateTime.UtcNow)
            .ToListAsync(ct);

        var ids = pendingIds.Concat(retryIds).Distinct().ToList();
        channel.EnqueueRange(ids, label);
        return ids.Count;
    }

    /// <summary>
    /// The last path segment of a (relative or absolute) folder path, for a human-readable run
    /// label — e.g. "/music/Kanye West" → "Kanye West". Falls back to the raw path.
    /// </summary>
    internal static string FolderDisplayName(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>
    /// Resolves a folder path (relative to the source root, as emitted by the directory tree, or an
    /// absolute source path) to the absolute, forward-slash-normalized prefix used to match
    /// <see cref="SongMetadata.SourcePath"/> values beneath it.
    /// </summary>
    internal static string ResolveFolderPrefix(string? sourceDirectory, string path)
    {
        static string Normalize(string? p) => (p ?? string.Empty).Replace('\\', '/').TrimEnd('/');

        var root = Normalize(sourceDirectory);
        var rel = Normalize(path);
        if (rel.Length == 0)
            return root;
        if (root.Length > 0 && rel.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return rel;
        return root.Length == 0 ? rel : $"{root}/{rel}";
    }

    private static IResult StartPurge(
        string mode,
        JobManager jobManager,
        IServiceScopeFactory scopeFactory,
        PurgeStatusTracker tracker,
        Guid ownerUserId,
        ILoggerFactory loggerFactory,
        Func<IPipelinePurgeService, Guid, Guid, CancellationToken, Task<PurgeResult>> run)
    {
        if (!jobManager.TryStartJob(JobType.Purge, out var jobId, out var cancellationToken))
            return Results.Conflict(new { message = "A purge is already running. Wait for it to finish." });

        var logger = loggerFactory.CreateLogger("MusicHoarder.Api.Pipeline.PipelinePurge");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IPipelinePurgeService>();
                await run(service, jobId, ownerUserId, cancellationToken);
                jobManager.SignalComplete(JobType.Purge, jobId);
            }
            catch (OperationCanceledException)
            {
                jobManager.SignalComplete(JobType.Purge, jobId, cancelled: true);
                tracker.Fail("Purge was cancelled.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Purge ({Mode}) failed", mode);
                jobManager.SignalFailed(JobType.Purge, jobId);
                tracker.Fail(ex.Message);
            }
        });

        return Results.Accepted("/api/enrichment/purge-status", new { jobId, mode });
    }

    private static IResult StreamProgress(
        HttpContext context,
        JobManager jobManager,
        ScanProgressTracker scanTracker,
        FingerprintProgressTracker fingerprintTracker,
        EnrichmentProgressTracker enrichmentTracker,
        LibraryBuilderProgressTracker buildTracker,
        DownloadProgressTracker downloadTracker)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers.Append("X-Accel-Buffering", "no");

        var ct = context.RequestAborted;

        async IAsyncEnumerable<string> StreamJson([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var wasRunning = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = ProgressSnapshotFactory.Create(
                    jobManager, scanTracker, fingerprintTracker, enrichmentTracker, buildTracker, downloadTracker);
                yield return JsonSerializer.Serialize(snapshot, SseJsonOptions);

                if (snapshot.IsComplete && wasRunning)
                    yield break;

                if (!snapshot.IsComplete)
                    wasRunning = true;

                await Task.Delay(1000, cancellationToken);
            }
        }

        return Results.ServerSentEvents(StreamJson(ct));
    }
}
