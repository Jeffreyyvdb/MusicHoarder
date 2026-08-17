using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Instance-to-instance sync surface. <c>/check</c> and <c>/upload</c> are machine-to-machine,
/// gated exclusively by <see cref="SyncApiKeyFilter"/> (they're allowlisted past the cookie-session
/// middleware and 404 unless this instance is a configured receiver). <c>/status</c> is a normal
/// cookie-authed, owner-only read for the settings UI.
/// </summary>
public static class SyncEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").WithTags("Sync");

        var m2m = group.MapGroup("")
            .AddEndpointFilter<SyncApiKeyFilter>();

        m2m.MapPost("/check", Check);
        m2m.MapPost("/upload", Upload).DisableAntiforgery();
        m2m.MapPost("/like", Like);

        group.MapGet("/status", Status).RequireOwner();
        group.MapPost("/requeue", Requeue)
            .WithSummary("Re-arm every settled outbox row (Synced/SkippedRemoteBetter/Failed) so the push sweep re-verifies each track against the remote. Tracks the remote already holds just re-check; missing or byte-different remote copies re-upload.")
            .RequireOwner();
        group.MapPost("/prune-duplicates", PruneDuplicates)
            .WithSummary("Receive-side cleanup: soft-delete redundant managed synced-source copies that share a fingerprint with a copy being kept, and remove their files. Dry-run unless apply=true; never touches a row that owns a destination file or a source outside the managed synced dir.")
            .RequireOwner();
    }

    private static async Task<IResult> Like(
        SyncLikeRequest request, ISyncIngestService ingest, CancellationToken ct)
    {
        var hasIdentity = !string.IsNullOrWhiteSpace(request.Fingerprint)
            || !string.IsNullOrWhiteSpace(request.AcoustIdTrackId)
            || !string.IsNullOrWhiteSpace(request.MusicBrainzId)
            || (!string.IsNullOrWhiteSpace(request.Artist) && !string.IsNullOrWhiteSpace(request.Title));
        if (!hasIdentity)
            return Results.UnprocessableEntity(new { error = "request_has_no_identity" });

        var response = await ingest.ApplyLikeAsync(request, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> Check(
        SyncCheckRequest request, ISyncIngestService ingest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Extension))
            return Results.UnprocessableEntity(new { error = "extension_required" });
        var response = await ingest.CheckAsync(request, ct);
        return Results.Ok(response);
    }

    /// <summary>
    /// Multipart upload: part <c>metadata</c> = <see cref="SyncTrackPayload"/> JSON, part
    /// <c>file</c> = audio bytes. Size limits come from <see cref="SyncOptions.MaxUploadBytes"/> —
    /// Kestrel's 30 MB default is far below a FLAC track — applied per-request so the cap is
    /// config-driven rather than a compile-time attribute.
    /// </summary>
    private static async Task<IResult> Upload(
        HttpRequest request,
        ISyncIngestService ingest,
        IOptionsMonitor<SyncOptions> options,
        CancellationToken ct)
    {
        var maxBytes = options.CurrentValue.MaxUploadBytes;

        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
            bodySizeFeature.MaxRequestBodySize = maxBytes;
        request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
        {
            MultipartBodyLengthLimit = maxBytes,
            ValueLengthLimit = 4 * 1024 * 1024, // metadata JSON incl. full synced lyrics
        }));

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Results.Json(new { error = "upload_too_large_or_malformed", detail = ex.Message },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var metadataJson = form["metadata"].ToString();
        if (string.IsNullOrWhiteSpace(metadataJson))
            return Results.UnprocessableEntity(new { error = "metadata_part_required" });

        SyncTrackPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SyncTrackPayload>(metadataJson, JsonOpts);
        }
        catch (JsonException)
        {
            payload = null;
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Extension))
            return Results.UnprocessableEntity(new { error = "invalid_metadata" });

        // A track with no identity at all can never be matched or meaningfully replaced later.
        var hasAnyIdentity = !string.IsNullOrWhiteSpace(payload.Fingerprint)
            || !string.IsNullOrWhiteSpace(payload.AcoustIdTrackId)
            || !string.IsNullOrWhiteSpace(payload.MusicBrainzId)
            || (!string.IsNullOrWhiteSpace(payload.Artist) && !string.IsNullOrWhiteSpace(payload.Title));
        if (!hasAnyIdentity)
            return Results.UnprocessableEntity(new { error = "payload_has_no_identity" });

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.UnprocessableEntity(new { error = "file_part_required" });
        if (file.Length > maxBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        await using var stream = file.OpenReadStream();
        var response = await ingest.IngestAsync(payload, stream, ct);
        return Results.Ok(response);
    }

    /// <summary>
    /// Push-side maintenance: flips every settled outbox row back to Pending. The sweep then runs
    /// each track through check → (upload) again, which is how remote copies that went missing or
    /// stale (e.g. the shared-"Unknown Title"-destination bug pushed one file's bytes under many
    /// songs' metadata) get healed — the byte-size probe answers PresentDifferentBytes and the
    /// re-upload replaces the remote file in place.
    /// </summary>
    internal static async Task<IResult> Requeue(
        IOptionsMonitor<SyncOptions> options, MusicHoarderDbContext db, CancellationToken ct)
    {
        if (options.CurrentValue.Mode != SyncMode.Push)
            return Results.Conflict(new { message = "Sync requeue only applies to a push-mode instance." });

        // The per-user query filter scopes rows to the calling owner. Loop instead of ExecuteUpdate:
        // the volume is small (one row per built track) and the InMemory test provider lacks bulk ops.
        var rows = await db.TrackSyncStates
            .Where(s => s.Status == TrackSyncStatus.Synced
                || s.Status == TrackSyncStatus.SkippedRemoteBetter
                || s.Status == TrackSyncStatus.Failed)
            .ToListAsync(ct);
        foreach (var row in rows)
            row.Requeue();
        await db.SaveChangesAsync(ct);

        return Results.Accepted("/api/sync/status", new { requeued = rows.Count });
    }

    /// <summary>
    /// Receive-side cleanup for the copies a runaway ingest already wrote. Groups live owner rows by
    /// exact fingerprint, elects a keeper with the shared duplicate-detection ranking, and retires
    /// every other copy that lives under the managed synced dir and owns no destination file.
    /// <para>
    /// Two guards keep this non-destructive in the repo's sense: rows are soft-deleted, never removed,
    /// and files are only unlinked under <see cref="SyncOptions.SyncedSourceDirectory"/> — a scanned
    /// original elsewhere on disk is never touched. Runs as a dry run unless <paramref name="apply"/>.
    /// </para>
    /// <para>
    /// Loads every fingerprinted live row into memory: grouping keys are the fingerprints themselves,
    /// so there is no cheaper shape. Acceptable for a hand-run maintenance call, not for a sweep.
    /// </para>
    /// </summary>
    internal static async Task<IResult> PruneDuplicates(
        IOptionsMonitor<SyncOptions> options,
        MusicHoarderDbContext db,
        ILogger<SyncPruneLog> logger,
        bool apply = false,
        CancellationToken ct = default)
    {
        var managedDir = options.CurrentValue.SyncedSourceDirectory;
        if (string.IsNullOrWhiteSpace(managedDir))
            return Results.Conflict(new { message = "No managed synced-source directory is configured." });
        var managedRoot = Normalize(managedDir).TrimEnd('/') + "/";

        // The per-user query filter scopes this to the calling owner, which also keeps the demo
        // tenant out (repo rule for all-tenant queries).
        var live = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && s.Fingerprint != null && s.Fingerprint != "")
            .ToListAsync(ct);

        var prunable = live
            .GroupBy(s => s.Fingerprint!)
            .Where(g => g.Count() > 1)
            .SelectMany(g => IDuplicateDetectionService.RankKeeperFirst(g)
                .Skip(1) // the keeper always survives
                .Where(loser => Normalize(loser.SourcePath).StartsWith(managedRoot, StringComparison.Ordinal))
                .Where(loser => loser.DestinationPath is null))
            .OrderBy(s => s.Id)
            .ToList();

        var bytes = prunable.Sum(s => s.FileSizeBytes);
        var worst = prunable
            .GroupBy(s => $"{s.Artist} — {s.Title}")
            .Select(g => new { track = g.Key, copies = g.Count() })
            .OrderByDescending(g => g.copies)
            .Take(10)
            .ToList();

        if (!apply)
            return Results.Ok(new { dryRun = true, rows = prunable.Count, bytes, worst });

        var filesDeleted = 0;
        foreach (var row in prunable)
        {
            if (TryDeleteFile(row.SourcePath, logger))
                filesDeleted++;
            row.SoftDelete();
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Sync prune retired {Rows} redundant managed copies ({Files} files, {Bytes} bytes)",
            prunable.Count, filesDeleted, bytes);

        return Results.Ok(new { dryRun = false, rows = prunable.Count, filesDeleted, bytes, worst });
    }

    private static bool TryDeleteFile(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Sync prune could not delete {Path}", LogSanitizer.ForLog(path));
            return false;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Log category marker for <see cref="PruneDuplicates"/> (static endpoint class).</summary>
    internal sealed class SyncPruneLog;

    private static async Task<IResult> Status(
        IOptionsMonitor<SyncOptions> options, MusicHoarderDbContext db, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var outbox = await db.TrackSyncStates
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Count(TrackSyncStatus status) => outbox.FirstOrDefault(o => o.Status == status)?.Count ?? 0;

        return Results.Ok(new
        {
            mode = opts.Mode.ToString(),
            receiveConfigured = opts.IsReceiveConfigured,
            pushConfigured = opts.IsPushConfigured,
            outbox = new
            {
                pending = Count(TrackSyncStatus.Pending),
                uploading = Count(TrackSyncStatus.Uploading),
                synced = Count(TrackSyncStatus.Synced),
                skippedRemoteBetter = Count(TrackSyncStatus.SkippedRemoteBetter),
                failed = Count(TrackSyncStatus.Failed),
            },
        });
    }
}
