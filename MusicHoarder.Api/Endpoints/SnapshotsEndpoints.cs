using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The pipeline-performance timeline: a per-version history of enrichment-quality snapshots plus a
/// per-song diff between any two versions (which songs regressed / improved). All reads go through
/// the owner-filtered DbContext so a user only sees their own snapshots.
/// </summary>
public static class SnapshotsEndpoints
{
    public static IEndpointRouteBuilder MapSnapshotsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/snapshots", GetSnapshots)
            .WithName("GetSnapshots")
            .WithSummary("Pipeline-quality timeline: one aggregate point per captured version, oldest first.")
            .WithTags("Snapshots")
            .RequireOwner();

        app.MapGet("/api/snapshots/{id:int}", GetSnapshot)
            .WithName("GetSnapshot")
            .WithSummary("One snapshot's aggregates + parsed config + the config diff vs the previous snapshot.")
            .WithTags("Snapshots")
            .RequireOwner();

        app.MapGet("/api/snapshots/compare", CompareSnapshots)
            .WithName("CompareSnapshots")
            .WithSummary("Per-song diff between two snapshots: which songs regressed or improved. Defaults to latest vs previous.")
            .WithTags("Snapshots")
            .RequireOwner();

        app.MapPost("/api/snapshots", CaptureSnapshot)
            .WithName("CaptureSnapshot")
            .WithSummary("Force-capture a snapshot now (de-dups if nothing changed).")
            .WithTags("Snapshots")
            .RequireOwner();

        app.MapDelete("/api/snapshots/{id:int}", DeleteSnapshot)
            .WithName("DeleteSnapshot")
            .WithSummary("Delete one snapshot (and its per-song rows).")
            .WithTags("Snapshots")
            .RequireOwner();

        return app;
    }

    // --- list / detail ---

    private static async Task<IResult> GetSnapshots(MusicHoarderDbContext db, CancellationToken ct)
    {
        var rows = await db.EnrichmentSnapshots
            .OrderBy(e => e.CapturedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(SummaryDto));
    }

    private static async Task<IResult> GetSnapshot(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var snap = await db.EnrichmentSnapshots.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (snap is null) return Results.NotFound(new { message = $"Snapshot {id} not found." });

        var prev = await db.EnrichmentSnapshots
            .Where(e => e.CapturedAtUtc < snap.CapturedAtUtc || (e.CapturedAtUtc == snap.CapturedAtUtc && e.Id < snap.Id))
            .OrderByDescending(e => e.CapturedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            summary = SummaryDto(snap),
            config = ParseJson(snap.ConfigJson),
            previousSnapshotId = prev?.Id,
            configDiff = prev is null ? [] : DiffConfig(prev.ConfigJson, snap.ConfigJson),
        });
    }

    // --- compare ---

    private static async Task<IResult> CompareSnapshots(int? from, int? to, MusicHoarderDbContext db, CancellationToken ct)
    {
        // Resolve defaults: to = latest, from = the one before it.
        var ordered = db.EnrichmentSnapshots.OrderByDescending(e => e.CapturedAtUtc).ThenByDescending(e => e.Id);

        EnrichmentSnapshot? toSnap = to is { } toId
            ? await db.EnrichmentSnapshots.FirstOrDefaultAsync(e => e.Id == toId, ct)
            : await ordered.FirstOrDefaultAsync(ct);
        if (toSnap is null) return Results.NotFound(new { message = "No snapshot to compare against." });

        EnrichmentSnapshot? fromSnap = from is { } fromId
            ? await db.EnrichmentSnapshots.FirstOrDefaultAsync(e => e.Id == fromId, ct)
            : await db.EnrichmentSnapshots
                .Where(e => e.CapturedAtUtc < toSnap.CapturedAtUtc || (e.CapturedAtUtc == toSnap.CapturedAtUtc && e.Id < toSnap.Id))
                .OrderByDescending(e => e.CapturedAtUtc).ThenByDescending(e => e.Id)
                .FirstOrDefaultAsync(ct);
        if (fromSnap is null)
            return Results.BadRequest(new { message = "No earlier snapshot to compare from." });

        var fromSongs = await LoadSongStatesAsync(db, fromSnap.Id, ct);
        var toSongs = await LoadSongStatesAsync(db, toSnap.Id, ct);

        var regressed = new List<(int SongId, SnapshotSongState From, SnapshotSongState To, string[] Reasons)>();
        var improved = new List<(int SongId, SnapshotSongState From, SnapshotSongState To, string[] Reasons)>();

        foreach (var (songId, toState) in toSongs)
        {
            if (!fromSongs.TryGetValue(songId, out var fromState)) continue; // only songs present in both

            var (kind, reasons) = SnapshotComparison.Classify(fromState, toState);
            if (kind == SnapshotChangeKind.Regressed) regressed.Add((songId, fromState, toState, reasons));
            else if (kind == SnapshotChangeKind.Improved) improved.Add((songId, fromState, toState, reasons));
        }

        // Worst regressions first (largest status/score drop), best improvements first.
        regressed.Sort((a, b) => SnapshotComparison.Severity(a.From, a.To).CompareTo(SnapshotComparison.Severity(b.From, b.To)));
        improved.Sort((a, b) => SnapshotComparison.Severity(b.From, b.To).CompareTo(SnapshotComparison.Severity(a.From, a.To)));

        var songIds = regressed.Select(r => r.SongId).Concat(improved.Select(i => i.SongId)).Distinct().ToList();
        var meta = await db.Songs
            .Where(s => songIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Artist, s.Title, s.SourcePath, s.FileName })
            .ToDictionaryAsync(s => s.Id, ct);

        object ToDto((int SongId, SnapshotSongState From, SnapshotSongState To, string[] Reasons) r)
        {
            meta.TryGetValue(r.SongId, out var m);
            return new
            {
                songId = r.SongId,
                artist = m?.Artist,
                title = m?.Title,
                sourcePath = m?.SourcePath,
                fileName = m?.FileName,
                reasons = r.Reasons,
                from = StateDto(r.From),
                to = StateDto(r.To),
            };
        }

        return Results.Ok(new
        {
            from = SummaryDto(fromSnap),
            to = SummaryDto(toSnap),
            comparedSongs = toSongs.Keys.Count(k => fromSongs.ContainsKey(k)),
            regressedCount = regressed.Count,
            improvedCount = improved.Count,
            regressed = regressed.Select(ToDto),
            improved = improved.Select(ToDto),
        });
    }

    private static async Task<Dictionary<int, SnapshotSongState>> LoadSongStatesAsync(
        MusicHoarderDbContext db, int snapshotId, CancellationToken ct)
    {
        var rows = await db.EnrichmentSnapshotSongs
            .Where(s => s.SnapshotId == snapshotId)
            .Select(s => new { s.SongId, s.EnrichmentStatus, s.MatchConfidence, s.MatchedBy, s.AiScore, s.AiVerdict })
            .ToListAsync(ct);
        return rows.ToDictionary(
            r => r.SongId,
            r => new SnapshotSongState(r.EnrichmentStatus, r.MatchConfidence, r.MatchedBy, r.AiScore, r.AiVerdict));
    }

    // --- capture / delete ---

    private record CaptureRequest(string? Label);

    private static async Task<IResult> CaptureSnapshot(
        CaptureRequest? request, IEnrichmentSnapshotService snapshots, IOwnerLookupService ownerLookup, CancellationToken ct)
    {
        var snap = await snapshots.CaptureAsync(
            ownerLookup.OwnerUserId, SnapshotTrigger.Manual, request?.Label, ct);

        if (snap is null)
            return Results.Ok(new { captured = false, reason = "No change since the last snapshot." });

        return Results.Ok(new { captured = true, snapshot = SummaryDto(snap) });
    }

    private static async Task<IResult> DeleteSnapshot(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var snap = await db.EnrichmentSnapshots.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (snap is null) return Results.NotFound(new { message = $"Snapshot {id} not found." });

        db.EnrichmentSnapshots.Remove(snap);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = id });
    }

    // --- mapping helpers ---

    private static object SummaryDto(EnrichmentSnapshot e)
    {
        var eligible = e.MatchedCount + e.NeedsReviewCount + e.FailedCount;
        return new
        {
            id = e.Id,
            capturedAtUtc = e.CapturedAtUtc,
            trigger = e.Trigger.ToString(),
            triggerLabel = e.TriggerLabel,
            version = e.Version,
            configHash = e.ConfigHash,
            totalSongs = e.TotalSongs,
            matched = e.MatchedCount,
            needsReview = e.NeedsReviewCount,
            failed = e.FailedCount,
            pending = e.PendingCount,
            duplicates = e.DuplicateCount,
            buildDone = e.BuildDoneCount,
            // Share of resolved (non-pending, non-duplicate) songs that auto-matched.
            matchRate = eligible == 0 ? (double?)null : Math.Round((double)e.MatchedCount / eligible, 4),
            avgMatchConfidence = e.AvgMatchConfidence,
            providerMatched = ParseJson(e.ProviderMatchedJson),
            graded = e.GradedCount,
            avgAiScore = e.AvgAiScore,
            ai = new
            {
                excellent = e.AiExcellent,
                good = e.AiGood,
                questionable = e.AiQuestionable,
                wrong = e.AiWrong,
                ungradeable = e.AiUngradeable,
            },
        };
    }

    private static object StateDto(SnapshotSongState s) => new
    {
        status = s.Status.ToString(),
        confidence = s.Confidence,
        matchedBy = s.MatchedBy,
        aiScore = s.AiScore,
        aiVerdict = s.AiVerdict?.ToString(),
    };

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Flattens both config JSONs to dotted leaf paths and returns the entries that differ.</summary>
    private static List<object> DiffConfig(string fromJson, string toJson)
    {
        var fromFlat = new Dictionary<string, string?>();
        var toFlat = new Dictionary<string, string?>();
        try { Flatten(JsonSerializer.Deserialize<JsonElement>(fromJson), "", fromFlat); } catch (JsonException) { }
        try { Flatten(JsonSerializer.Deserialize<JsonElement>(toJson), "", toFlat); } catch (JsonException) { }

        var keys = fromFlat.Keys.Union(toFlat.Keys).OrderBy(k => k);
        var diffs = new List<object>();
        foreach (var key in keys)
        {
            fromFlat.TryGetValue(key, out var fv);
            toFlat.TryGetValue(key, out var tv);
            if (fv != tv) diffs.Add(new { key, from = fv, to = tv });
        }
        return diffs;
    }

    private static void Flatten(JsonElement el, string prefix, Dictionary<string, string?> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}", into);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    Flatten(item, $"{prefix}[{i++}]", into);
                break;
            default:
                into[prefix] = el.ToString();
                break;
        }
    }
}
