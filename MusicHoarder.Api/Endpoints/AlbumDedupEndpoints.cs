using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

public record AlbumMergeRequest(string Artist, string KeepAlbum, string MergeAlbum);

public record AlbumDismissRequest(string Artist, string AlbumA, string AlbumB);

/// <summary>
/// Album-level dedup for pairs <c>AlbumGroupKey</c> keeps apart ("The Blueprint 3" vs
/// "Blueprint 3"). Merging rewrites the merge-side titles to the kept spelling; once both halves
/// share one group key, the existing reconciler/split-heal converges year/release-id/album-artist —
/// no identity logic is duplicated here.
/// </summary>
public static class AlbumDedupEndpoints
{
    public static IEndpointRouteBuilder MapAlbumDedupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/library/albums/duplicates", Detect)
            .WithName("GetAlbumDuplicates")
            .WithSummary("Near-duplicate album pairs under one artist that the exact grouping key misses (leading-\"the\", \"&\" vs \"and\", near-miss spellings).")
            .WithTags("Library").RequireOwner();

        app.MapPost("/api/library/albums/merge", Merge)
            .WithName("MergeAlbums")
            .WithSummary("Rewrite one album spelling onto the other so both halves share a grouping key; built tracks re-queue for re-tag and the identity heal converges the rest.")
            .WithTags("Library").RequireOwner();

        app.MapPost("/api/library/albums/dismiss", Dismiss)
            .WithName("DismissAlbumDuplicates")
            .WithSummary("Mark two album titles as NOT the same album; the decision persists across detections.")
            .WithTags("Library").RequireOwner();

        return app;
    }

    internal static async Task<IResult> Detect(
        IAlbumDuplicateDetector detector, ICurrentUserAccessor currentUser, CancellationToken ct)
    {
        var pairs = await detector.DetectAsync(currentUser.UserId, ct);
        return Results.Ok(new { count = pairs.Count, pairs });
    }

    internal static async Task<IResult> Merge(
        AlbumMergeRequest request, MusicHoarderDbContext db, JobManager jobManager, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Artist)
            || string.IsNullOrWhiteSpace(request.KeepAlbum)
            || string.IsNullOrWhiteSpace(request.MergeAlbum))
            return Results.BadRequest(new { message = "artist, keepAlbum and mergeAlbum are required." });

        var keepAlbum = request.KeepAlbum.Trim();
        var artistKey = AlbumGroupKey.ComputeArtistKey(request.Artist);
        var mergeKey = AlbumGroupKey.ComputeAlbumKey(request.MergeAlbum);
        if (mergeKey == AlbumGroupKey.ComputeAlbumKey(keepAlbum))
            return Results.BadRequest(new { message = "keepAlbum and mergeAlbum already share one grouping key — nothing to merge." });

        // The per-user query filter scopes this to the caller's library. Match by logical-album key
        // (mirrors POST /api/enrichment/rebuild/album) so every spelling on the merge side is caught.
        var candidates = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .Where(s => s.Album != null && s.Album != "")
            .ToListAsync(ct);
        var songs = candidates
            .Where(s => AlbumGroupKey.For(s) is { } key
                && key.ArtistKey == artistKey
                && key.AlbumKey == mergeKey)
            .ToList();

        if (songs.Count == 0)
            return Results.NotFound(new { message = $"No songs found for album '{request.MergeAlbum}' by '{request.Artist}'." });

        var now = DateTime.UtcNow;
        var requeued = 0;
        foreach (var song in songs)
        {
            if (string.Equals(song.Album, keepAlbum, StringComparison.Ordinal))
                continue;

            song.CaptureOriginalMetadata();
            db.SongMetadataChanges.Add(new SongMetadataChange
            {
                SongId = song.Id,
                FieldName = nameof(SongMetadata.Album),
                OldValue = song.Album,
                NewValue = keepAlbum,
                Source = "album-merge",
                Confidence = 1.0,
                CreatedAtUtc = now,
                AppliedAtUtc = now,
            });
            song.Album = keepAlbum;

            // Re-tag/relocate built files under the kept album (see ArtistCreditHealer for the
            // RequeueForRetag semantics).
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        await db.SaveChangesAsync(ct);

        // Wake the builder; a 409 from an already-running build isn't an error.
        jobManager.TryStartJob(JobType.Build, out var jobId, out _);

        return Results.Ok(new
        {
            artist = request.Artist,
            keepAlbum,
            mergedAlbum = request.MergeAlbum,
            songsUpdated = songs.Count(s => string.Equals(s.Album, keepAlbum, StringComparison.Ordinal)),
            requeued,
            jobId,
        });
    }

    internal static async Task<IResult> Dismiss(
        AlbumDismissRequest request, MusicHoarderDbContext db, ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Artist)
            || string.IsNullOrWhiteSpace(request.AlbumA)
            || string.IsNullOrWhiteSpace(request.AlbumB))
            return Results.BadRequest(new { message = "artist, albumA and albumB are required." });

        var artistKey = AlbumGroupKey.ComputeArtistKey(request.Artist);
        var keyA = TitleNormalizer.NormalizeForSearch(request.AlbumA);
        var keyB = TitleNormalizer.NormalizeForSearch(request.AlbumB);
        if (keyA.Length == 0 || keyB.Length == 0 || keyA == keyB)
            return Results.BadRequest(new { message = "The two album titles must normalize to distinct non-empty keys." });

        var (low, high) = string.CompareOrdinal(keyA, keyB) <= 0 ? (keyA, keyB) : (keyB, keyA);
        var ownerId = currentUser.UserId;

        var exists = await db.DedupDismissals.AnyAsync(
            d => d.Kind == DedupDismissalKind.AlbumPair
                && d.ScopeKey == artistKey && d.KeyLow == low && d.KeyHigh == high,
            ct);
        if (!exists)
        {
            db.DedupDismissals.Add(new DedupDismissal
            {
                OwnerUserId = ownerId,
                Kind = DedupDismissalKind.AlbumPair,
                ScopeKey = artistKey,
                KeyLow = low,
                KeyHigh = high,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { dismissed = true });
    }
}
