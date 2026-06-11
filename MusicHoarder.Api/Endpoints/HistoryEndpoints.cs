using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The global "what did MusicHoarder change in my library" feed. Reads <see cref="LibraryWriteEvent"/>s
/// (recorded at build / re-tag time, so they reflect exactly what landed in the files Navidrome reads)
/// and rolls the raw per-field changes up into human-readable summaries that the UI can expand back to
/// the underlying field diffs. Time-filtered; owner-scoped by the EF global query filter.
/// </summary>
public static class HistoryEndpoints
{
    private const int MaxWindowRows = 5000;
    private const int DefaultTake = 50;
    private const int MaxTake = 200;
    private const int DefaultWindowDays = 30;

    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/history", GetHistory).WithName("GetHistory").RequireOwner();
        return app;
    }

    internal static async Task<IResult> GetHistory(
        MusicHoarderDbContext db,
        DateTime? from,
        DateTime? to,
        string? artist,
        string? album,
        DateTime? cursor,
        int? take)
    {
        var upper = to ?? DateTime.UtcNow;
        var lower = from ?? upper.AddDays(-DefaultWindowDays);
        var pageSize = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var query = db.LibraryWriteEvents
            .Where(e => e.WrittenAtUtc >= lower && e.WrittenAtUtc <= upper);

        if (cursor is { } c)
        {
            query = query.Where(e => e.WrittenAtUtc < c);
        }
        if (!string.IsNullOrWhiteSpace(artist))
        {
            query = query.Where(e => e.AlbumArtist == artist);
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            query = query.Where(e => e.Album == album);
        }

        // Materialize a bounded, newest-first window; the rollup runs in memory (so it also dodges the
        // EF InMemory provider's GroupBy limitations in tests).
        var events = await query
            .OrderByDescending(e => e.WrittenAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(MaxWindowRows)
            .ToListAsync();

        // Resolve track titles for the raw-change rows in one query (owner-scoped via the query filter).
        var songIds = events.Where(e => e.SongId is not null).Select(e => e.SongId!.Value).Distinct().ToList();
        var titleById = await db.Songs
            .Where(s => songIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title })
            .ToDictionaryAsync(s => s.Id, s => s.Title);

        var summaries = RollUp(events, titleById);
        var page = summaries.Take(pageSize).ToList();

        var hasMore = summaries.Count > pageSize || events.Count == MaxWindowRows;
        DateTime? nextCursor = hasMore && page.Count > 0
            ? page.SelectMany(s => s.Changes).Min(rc => rc.WrittenAtUtc)
            : null;

        return Results.Ok(new HistoryFeedResponse(page, nextCursor?.ToString("O"), events.Count));
    }

    // Groups raw write events into the layered summaries the feed shows. Cover writes are their own
    // album-level summary; track-tag writes are split per album folder into consolidation / artist-rename
    // / year-correction / tag-rewrite buckets, each carrying its underlying field changes for the expand.
    // Internal so the album timeline endpoint reuses the exact same rollup semantics.
    internal static List<HistorySummary> RollUp(
        IReadOnlyList<LibraryWriteEvent> events, IReadOnlyDictionary<int, string?> titleById)
    {
        var summaries = new List<HistorySummary>();

        foreach (var coverGroup in events
            .Where(e => e.Kind == LibraryWriteEventKind.AlbumCoverWritten)
            .GroupBy(e => e.AlbumFolder ?? ""))
        {
            var latest = coverGroup.MaxBy(e => e.WrittenAtUtc)!;
            summaries.Add(new HistorySummary(
                Id: StableId("cover", latest.AlbumFolder, null, null),
                Kind: "cover",
                Headline: $"Cover art added for {Quote(latest.Album)}",
                AlbumArtist: latest.AlbumArtist,
                Album: latest.Album,
                TrackCount: 1,
                LatestWrittenAtUtc: latest.WrittenAtUtc,
                RunId: latest.RunId,
                Changes: [ToRawChange(latest, titleById)]));
        }

        var trackEvents = events.Where(e => e.Kind == LibraryWriteEventKind.TrackTagsWritten).ToList();
        foreach (var albumGroup in trackEvents.GroupBy(e => e.AlbumFolder ?? ""))
        {
            var albumArtist = albumGroup.First().AlbumArtist;
            var album = albumGroup.First().Album;

            var byField = albumGroup.ToLookup(e => Classify(e.FieldName));

            // Consolidation: album-identity churn across the folder's tracks.
            var consolidation = byField["consolidation"].ToList();
            if (consolidation.Count > 0)
            {
                var trackCount = consolidation.Select(e => e.SongId).Distinct().Count();
                var releaseCount = consolidation
                    .Where(e => e.FieldName == "MusicBrainzReleaseId" && e.OldValue is not null)
                    .Select(e => e.OldValue).Distinct().Count();
                var headline = releaseCount > 1
                    ? $"Album {Quote(album)} consolidated from {releaseCount} releases across {Plural(trackCount, "track")}"
                    : $"Album {Quote(album)} consolidated across {Plural(trackCount, "track")}";
                summaries.Add(BuildSummary("consolidation", headline, albumArtist, album, trackCount, consolidation, titleById));
            }

            // Artist rename: group by old → new.
            foreach (var rename in byField["artist"]
                .GroupBy(e => (e.OldValue, e.NewValue)))
            {
                var trackCount = rename.Select(e => e.SongId).Distinct().Count();
                var headline = rename.Key.OldValue is null
                    ? $"Artist set to {Quote(rename.Key.NewValue)} on {Plural(trackCount, "track")}"
                    : $"Artist renamed {Quote(rename.Key.OldValue)} → {Quote(rename.Key.NewValue)} on {Plural(trackCount, "track")}";
                summaries.Add(BuildSummary("artist-rename", headline, albumArtist, album, trackCount, rename.ToList(), titleById));
            }

            // Year correction: group by old → new.
            foreach (var year in byField["year"]
                .GroupBy(e => (e.OldValue, e.NewValue)))
            {
                var trackCount = year.Select(e => e.SongId).Distinct().Count();
                var headline = year.Key.OldValue is null
                    ? $"Year set to {year.Key.NewValue} for {Quote(album)}"
                    : $"Year corrected {year.Key.OldValue} → {year.Key.NewValue} for {Quote(album)}";
                summaries.Add(BuildSummary("year-correction", headline, albumArtist, album, trackCount, year.ToList(), titleById));
            }

            // Everything else: a generic tag rewrite.
            var tags = byField["tags"].ToList();
            if (tags.Count > 0)
            {
                var trackCount = tags.Select(e => e.SongId).Distinct().Count();
                summaries.Add(BuildSummary(
                    "tags",
                    $"Tags updated on {Plural(trackCount, "track")} of {Quote(album)}",
                    albumArtist, album, trackCount, tags, titleById));
            }
        }

        return summaries
            .OrderByDescending(s => s.LatestWrittenAtUtc)
            .ToList();
    }

    private static HistorySummary BuildSummary(
        string kind, string headline, string? albumArtist, string? album, int trackCount,
        List<LibraryWriteEvent> source, IReadOnlyDictionary<int, string?> titleById)
    {
        var changes = source
            .OrderByDescending(e => e.WrittenAtUtc)
            .Select(e => ToRawChange(e, titleById))
            .ToList();
        return new HistorySummary(
            Id: StableId(kind, album, headline, null),
            Kind: kind,
            Headline: headline,
            AlbumArtist: albumArtist,
            Album: album,
            TrackCount: trackCount,
            LatestWrittenAtUtc: source.Max(e => e.WrittenAtUtc),
            RunId: source.First().RunId,
            Changes: changes);
    }

    private static HistoryRawChange ToRawChange(LibraryWriteEvent e, IReadOnlyDictionary<int, string?> titleById)
        => new(
            e.SongId,
            e.SongId is { } id && titleById.TryGetValue(id, out var title) ? title : null,
            e.FieldName ?? "",
            e.OldValue,
            e.NewValue,
            e.IsAlbumIdentityField,
            e.WrittenAtUtc);

    private static string Classify(string? field) => field switch
    {
        "Album" or "MusicBrainzReleaseId" or "MusicBrainzReleaseGroupId" => "consolidation",
        "Artist" or "AlbumArtist" or "AlbumArtistMusicBrainzId" => "artist",
        "Year" => "year",
        _ => "tags",
    };

    private static string Quote(string? value) => string.IsNullOrWhiteSpace(value) ? "(unknown)" : $"'{value}'";

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string StableId(string kind, string? album, string? a, string? b)
        => $"{kind}|{album}|{a}|{b}";
}

public record HistoryRawChange(
    int? SongId,
    string? TrackTitle,
    string Field,
    string? OldValue,
    string? NewValue,
    bool IsAlbumIdentity,
    DateTime WrittenAtUtc);

public record HistorySummary(
    string Id,
    string Kind,
    string Headline,
    string? AlbumArtist,
    string? Album,
    int TrackCount,
    DateTime LatestWrittenAtUtc,
    Guid? RunId,
    IReadOnlyList<HistoryRawChange> Changes);

public record HistoryFeedResponse(
    IReadOnlyList<HistorySummary> Summaries,
    string? NextCursor,
    int TotalEventsInWindow);
