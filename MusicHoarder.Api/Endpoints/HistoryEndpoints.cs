using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.History;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The global "what has MusicHoarder been doing" feed.
/// <para>
/// It merges one <see cref="IActivitySource"/> per domain — tracks arriving, enrichment, destination
/// writes, lyrics, music videos, artwork, likes, instance sync, curation, and the pipeline itself —
/// into a single time-ordered list of <see cref="HistorySummary"/> entries, sliceable by category.
/// </para>
/// <para>
/// Only the destination-write half reads a purpose-built log
/// (<see cref="LibraryWriteEvent"/>, because a tag diff is unrecoverable after the fact). Every other
/// source <b>derives</b> its entries from the rows the pipeline already writes, which is why the feed
/// is full of real history the moment it ships rather than starting empty. See
/// <see cref="IActivitySource"/> for the trade-off that buys.
/// </para>
/// </summary>
public static class HistoryEndpoints
{
    private const int MaxWindowRows = 5000;
    private const int DefaultTake = 50;
    private const int MaxTake = 200;
    private const int DefaultWindowDays = 30;

    /// <summary>Per-source row budget for one feed request — see <see cref="ActivityWindow"/>.</summary>
    internal const int MaxRowsPerSource = 4000;

    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/history", GetHistory).WithName("GetHistory").RequireAdmin();
        return app;
    }

    internal static async Task<IResult> GetHistory(
        MusicHoarderDbContext db,
        IEnumerable<IActivitySource> sources,
        DateTime? from,
        DateTime? to,
        string? artist,
        string? album,
        string? category,
        bool? problems,
        string? cursor,
        int? take,
        CancellationToken ct)
    {
        var upper = to ?? DateTime.UtcNow;
        var lower = from ?? upper.AddDays(-DefaultWindowDays);
        var pageSize = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        var window = new ActivityWindow(lower, upper, MaxRowsPerSource);

        var wanted = ParseCategories(category);

        // Sources share the request-scoped DbContext, so they run one after another rather than in
        // parallel. Each is a handful of bounded, owner-filtered queries; the whole feed is one admin
        // page load, not a hot path. Every source runs even under a category filter — see
        // IActivitySource for why skipping the filtered-out ones would break the chips' counts.
        var all = new List<HistorySummary>();
        foreach (var source in sources)
        {
            all.AddRange(await source.CollectAsync(window, ct));
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            all = all.Where(s => string.Equals(s.AlbumArtist, artist, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            all = all.Where(s => string.Equals(s.Album, album, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // "Only problems" narrows before the counts, so with it on the chips answer "where did things
        // go wrong" rather than "how much happened". An empty result then means something specific:
        // nothing went wrong, as opposed to nothing happened.
        if (problems == true)
        {
            all = all.Where(s => s.Tint is ActivityTint.Warn or ActivityTint.Error).ToList();
        }

        // Counts are taken before the category filter so the chips always show what selecting them
        // would reveal, not what the current selection happens to contain.
        var counts = all
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        if (wanted is not null)
        {
            all = all.Where(s => wanted.Contains(s.Category)).ToList();
        }

        var ordered = all
            .OrderByDescending(s => s.LatestWrittenAtUtc)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        // The cursor carries the last entry's (timestamp, id) so a sweep that stamped a thousand rows
        // in the same millisecond pages cleanly instead of losing the tail of that millisecond.
        if (ParseCursor(cursor) is var (cursorAt, cursorId) && cursorAt is { } at)
        {
            ordered = ordered
                .Where(s => s.LatestWrittenAtUtc < at
                    || (s.LatestWrittenAtUtc == at && string.CompareOrdinal(s.Id, cursorId) > 0))
                .ToList();
        }

        var page = ordered.Take(pageSize).ToList();
        var hasMore = ordered.Count > pageSize;
        var last = page.Count > 0 ? page[^1] : null;
        var nextCursor = hasMore && last is not null
            ? $"{last.LatestWrittenAtUtc:O}|{last.Id}"
            : null;

        return Results.Ok(new HistoryFeedResponse(page, nextCursor, all.Count, counts));
    }

    /// <summary>Parses the repeatable/comma-separated <c>category</c> filter. Null means "everything".</summary>
    private static HashSet<string>? ParseCategories(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var set = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .Where(c => ActivityCategory.All.Contains(c))
            .ToHashSet();
        return set.Count > 0 ? set : null;
    }

    private static (DateTime? At, string Id) ParseCursor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "");
        var pipe = raw.IndexOf('|');
        var timePart = pipe < 0 ? raw : raw[..pipe];
        var idPart = pipe < 0 ? "" : raw[(pipe + 1)..];
        return DateTime.TryParse(
            timePart, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? (parsed, idPart)
            : (null, "");
    }

    /// <summary>
    /// Groups raw destination-write events into the layered summaries the feed shows. Cover writes are
    /// their own album-level summary; track-tag writes are split per album folder into consolidation /
    /// artist-rename / year-correction / tag-rewrite buckets, each carrying its underlying field changes
    /// for the expand.
    /// <para><b>Shared contract.</b> <c>GET /api/albums/timeline</c> calls this directly and depends on
    /// the exact <see cref="HistorySummary.Kind"/> strings, the <see cref="HistorySummary.Id"/> shape
    /// (it is the dialog's list key) and the headline prose. Add new event types as new sources rather
    /// than reshaping this.</para>
    /// </summary>
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
                // Cover writes are real destination writes, but a reader looking for artwork looks
                // under Artwork — so the write-event rollup lends this one entry to that chip.
                Category: ActivityCategory.Artwork,
                Headline: $"Cover art added for {Quote(latest.Album)}",
                Detail: DescribeCoverSource(latest.NewValue),
                Tint: ActivityTint.Info,
                AlbumArtist: latest.AlbumArtist,
                Album: latest.Album,
                SongId: latest.SongId,
                TrackTitle: null,
                TrackCount: 1,
                LatestWrittenAtUtc: latest.WrittenAtUtc,
                RunId: latest.RunId,
                Changes: [ToRawChange(latest, titleById)]));
        }

        var trackEvents = events.Where(e => e.Kind == LibraryWriteEventKind.TrackTagsWritten).ToList();
        foreach (var albumGroup in trackEvents.GroupBy(e => e.AlbumFolder ?? ""))
        {
            var albumFolder = albumGroup.Key;
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
                summaries.Add(BuildSummary("consolidation", headline, albumFolder, albumArtist, album, trackCount, consolidation, titleById));
            }

            // Artist rename: group by old → new.
            foreach (var rename in byField["artist"]
                .GroupBy(e => (e.OldValue, e.NewValue)))
            {
                var trackCount = rename.Select(e => e.SongId).Distinct().Count();
                var headline = rename.Key.OldValue is null
                    ? $"Artist set to {Quote(rename.Key.NewValue)} on {Plural(trackCount, "track")}"
                    : $"Artist renamed {Quote(rename.Key.OldValue)} → {Quote(rename.Key.NewValue)} on {Plural(trackCount, "track")}";
                summaries.Add(BuildSummary("artist-rename", headline, albumFolder, albumArtist, album, trackCount, rename.ToList(), titleById));
            }

            // Year correction: group by old → new.
            foreach (var year in byField["year"]
                .GroupBy(e => (e.OldValue, e.NewValue)))
            {
                var trackCount = year.Select(e => e.SongId).Distinct().Count();
                var headline = year.Key.OldValue is null
                    ? $"Year set to {year.Key.NewValue} for {Quote(album)}"
                    : $"Year corrected {year.Key.OldValue} → {year.Key.NewValue} for {Quote(album)}";
                summaries.Add(BuildSummary("year-correction", headline, albumFolder, albumArtist, album, trackCount, year.ToList(), titleById));
            }

            // Everything else: a generic tag rewrite.
            var tags = byField["tags"].ToList();
            if (tags.Count > 0)
            {
                var trackCount = tags.Select(e => e.SongId).Distinct().Count();
                summaries.Add(BuildSummary(
                    "tags",
                    $"Tags updated on {Plural(trackCount, "track")} of {Quote(album)}",
                    albumFolder, albumArtist, album, trackCount, tags, titleById));
            }
        }

        return summaries
            .OrderByDescending(s => s.LatestWrittenAtUtc)
            .ToList();
    }

    /// <summary>Loads the destination-write window the feed and the album timeline both roll up.</summary>
    internal static async Task<(List<LibraryWriteEvent> Events, Dictionary<int, string?> Titles)> LoadWriteEventsAsync(
        MusicHoarderDbContext db, ActivityWindow window, CancellationToken ct)
    {
        var events = await db.LibraryWriteEvents
            .AsNoTracking()
            .Where(e => e.WrittenAtUtc >= window.FromUtc && e.WrittenAtUtc <= window.ToUtc)
            .OrderByDescending(e => e.WrittenAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(MaxWindowRows)
            .ToListAsync(ct);

        var songIds = events.Where(e => e.SongId is not null).Select(e => e.SongId!.Value).Distinct().ToList();
        var titleById = await db.Songs
            .AsNoTracking()
            .Where(s => songIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title })
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct);

        return (events, titleById);
    }

    private static HistorySummary BuildSummary(
        string kind, string headline, string? albumFolder, string? albumArtist, string? album, int trackCount,
        List<LibraryWriteEvent> source, IReadOnlyDictionary<int, string?> titleById)
    {
        var changes = source
            .OrderByDescending(e => e.WrittenAtUtc)
            .Select(e => ToRawChange(e, titleById))
            .ToList();
        return new HistorySummary(
            // Keyed on the album FOLDER (what RollUp groups by), not the display name: one album can
            // span two folders with the same name (rename/move), and name-keyed ids would collide —
            // producing duplicate keys that crash the timeline/history {#each}.
            Id: StableId(kind, albumFolder, headline, null),
            Kind: kind,
            Category: ActivityCategory.Written,
            Headline: headline,
            Detail: null,
            Tint: ActivityTint.Ok,
            AlbumArtist: albumArtist,
            Album: album,
            SongId: null,
            TrackTitle: null,
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

    /// <summary>Turns the cover event's <c>NewValue</c> ("written" / "fetched:spotify") into feed prose.</summary>
    private static string? DescribeCoverSource(string? newValue) => newValue switch
    {
        null or "" => null,
        "written" => "From the source folder's own artwork",
        var v when v.StartsWith("fetched:", StringComparison.Ordinal) => $"Fetched from {v["fetched:".Length..]}",
        _ => null,
    };

    private static string Classify(string? field) => field switch
    {
        "Album" or "MusicBrainzReleaseId" or "MusicBrainzReleaseGroupId" => "consolidation",
        "Artist" or "AlbumArtist" or "AlbumArtistMusicBrainzId" => "artist",
        "Year" => "year",
        _ => "tags",
    };

    private static string Quote(string? value) => ActivityText.Quote(value);

    private static string Plural(int count, string noun) => ActivityText.Plural(count, noun);

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
    DateTime WrittenAtUtc,
    /// <summary>Free-text description for entries that aren't a field diff (a provider name, an error).</summary>
    string? Detail = null);

public record HistorySummary(
    string Id,
    /// <summary>Fine-grained event type — drives the icon. See each source for the values it emits.</summary>
    string Kind,
    /// <summary>Filter bucket. One of <see cref="ActivityCategory"/>.</summary>
    string Category,
    string Headline,
    /// <summary>Optional second line: the provider, the reason, the error.</summary>
    string? Detail,
    /// <summary>One of <see cref="ActivityTint"/>.</summary>
    string Tint,
    string? AlbumArtist,
    string? Album,
    /// <summary>Set when the entry is about exactly one track, so the row can link straight to it.</summary>
    int? SongId,
    string? TrackTitle,
    int TrackCount,
    DateTime LatestWrittenAtUtc,
    Guid? RunId,
    IReadOnlyList<HistoryRawChange> Changes);

public record HistoryFeedResponse(
    IReadOnlyList<HistorySummary> Summaries,
    string? NextCursor,
    /// <summary>Entries (not field writes) matching the current filters in the window.</summary>
    int TotalEventsInWindow,
    /// <summary>Entry count per category before the category filter — the chips' numbers.</summary>
    IReadOnlyDictionary<string, int> CategoryCounts);
