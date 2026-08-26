using System.Globalization;
using MusicHoarder.Api.Endpoints;

namespace MusicHoarder.Api.History;

/// <summary>
/// The filter buckets the History feed slices by. Wire contract with the frontend's category chips —
/// the strings are sent verbatim, so renaming one is a breaking change for the page.
/// </summary>
public static class ActivityCategory
{
    /// <summary>Tracks arriving: downloads, wishlist adds, quality upgrades, album completion, new files found by a scan.</summary>
    public const string Acquired = "acquired";

    /// <summary>Identifying a track: provider matches, review outcomes, canonical tracklists.</summary>
    public const string Enriched = "enriched";

    /// <summary>What physically landed in the destination library — the original History feed.</summary>
    public const string Written = "written";

    public const string Lyrics = "lyrics";
    public const string Video = "video";
    public const string Artwork = "artwork";

    /// <summary>Likes and exported playlists — what the owner did with the music rather than to it.</summary>
    public const string Listening = "listening";

    /// <summary>Pushing the library elsewhere: the other MusicHoarder instance, Navidrome.</summary>
    public const string Sync = "sync";

    /// <summary>Tidying: dedup actions, duplicate links, quality grades, tracks that went away.</summary>
    public const string Curation = "curation";

    /// <summary>The machine itself: scans, fingerprinting, ingest runs.</summary>
    public const string Pipeline = "pipeline";

    /// <summary>Every category, in the order the frontend chips render them.</summary>
    public static readonly string[] All =
        [Acquired, Enriched, Written, Lyrics, Video, Artwork, Listening, Sync, Curation, Pipeline];
}

/// <summary>Severity of one feed entry. Mirrors the timeline's vocabulary so both can share a renderer.</summary>
public static class ActivityTint
{
    public const string Ok = "ok";
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Error = "err";
}

/// <summary>
/// The slice of time one feed request covers. <paramref name="MaxRowsPerSource"/> bounds every source's
/// underlying query so a busy month can't turn one page load into a table scan the size of the library.
/// </summary>
public readonly record struct ActivityWindow(DateTime FromUtc, DateTime ToUtc, int MaxRowsPerSource)
{
    public bool Covers(DateTime? at) => at is { } t && t >= FromUtc && t <= ToUtc;
}

/// <summary>
/// One domain's contribution to the History feed.
/// <para>
/// Sources <b>derive</b> their entries from the rows the pipeline already writes (a
/// <see cref="Persistence.WishlistItem"/>'s status, a song's <c>LyricsSyncCheckedAtUtc</c>, an
/// <see cref="Persistence.UpgradeRequest"/>'s completion) rather than reading a dedicated event log.
/// That is the same choice <see cref="Library.SongOriginResolver"/> and
/// <see cref="Library.DedupActionHistoryService"/> make, and for the same reason: a derived feed is
/// correct for rows that predate the feature, costs the hot pipeline paths nothing, and cannot drift
/// out of sync with the state it describes.
/// </para>
/// <para>
/// The trade-off, which the page states plainly: these are "last time X happened" stamps, so a track
/// whose lyrics were fetched twice appears once, at the later time. The feed is an activity view, not
/// an audit log. <see cref="Persistence.LibraryWriteEvent"/> stays the one true append-only log,
/// because a tag diff is genuinely unrecoverable after the fact.
/// </para>
/// </summary>
public interface IActivitySource
{
    /// <summary>
    /// Every entry this source can find in the window. Sources deliberately do <b>not</b> get to
    /// declare which categories they emit so the endpoint can skip them under a category filter: the
    /// chips carry counts, and a count is only useful if it says what pressing that chip would reveal.
    /// Skipping a source would zero the very numbers the reader is choosing between.
    /// </summary>
    Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct);
}

/// <summary>One track-level occurrence, before it is grouped into a feed entry.</summary>
public readonly record struct ActivityItem(
    int? SongId,
    string? TrackTitle,
    string? Album,
    string? AlbumArtist,
    DateTime AtUtc,
    string? Detail = null);

/// <summary>Shared sentence-building and grouping so every source phrases things the same way.</summary>
public static class ActivityText
{
    public static string Quote(string? value) => string.IsNullOrWhiteSpace(value) ? "(unknown)" : $"'{value}'";

    public static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// Renders a millisecond offset as seconds. Invariant on purpose: these strings go over the wire
    /// verbatim, so a server whose locale uses a decimal comma must not send "2,4s" to the page.
    /// </summary>
    public static string Seconds(int milliseconds) =>
        (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>Renders a whole number the same way, for the same reason.</summary>
    public static string Round(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Truncates free text (a provider error, a video title) to a length a feed row can show.</summary>
    public static string? Clip(string? value, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var flat = value.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    /// <summary>
    /// Builds one feed entry from its occurrences.
    /// <para><paramref name="idKey"/> must be unique across everything the feed returns for a window —
    /// this repo has shipped <c>each_key_duplicate</c> white-screens twice from a colliding list key,
    /// so it is built from a row id or an album folder, never a display name alone.</para>
    /// </summary>
    public static HistorySummary Summary(
        string category,
        string kind,
        string tint,
        string idKey,
        string headline,
        string? detail,
        IReadOnlyList<ActivityItem> items,
        Guid? runId = null)
    {
        var songIds = items.Where(i => i.SongId is not null).Select(i => i.SongId!.Value).Distinct().ToList();

        // Only claim an album when every occurrence agrees on one. A day's likes, or a wishlist sync,
        // spans albums — and labelling those with whichever one happened to sort first is worse than
        // saying nothing, because the row's album and artist links would then go somewhere wrong.
        var albums = items.Select(i => (i.AlbumArtist, i.Album)).Distinct().ToList();
        var (albumArtist, album) = albums.Count == 1 ? albums[0] : (null, null);

        return new HistorySummary(
            Id: $"{category}:{kind}|{idKey}",
            Kind: kind,
            Category: category,
            Headline: headline,
            Detail: detail,
            Tint: tint,
            AlbumArtist: albumArtist,
            Album: album,
            SongId: songIds.Count == 1 ? songIds[0] : null,
            TrackTitle: songIds.Count == 1
                ? items.First(i => i.SongId is not null).TrackTitle
                : items.Count == 1 ? items[0].TrackTitle : null,
            TrackCount: songIds.Count > 0 ? songIds.Count : items.Count,
            LatestWrittenAtUtc: items.Max(i => i.AtUtc),
            RunId: runId,
            Changes: items
                .OrderByDescending(i => i.AtUtc)
                .Select(i => new HistoryRawChange(
                    i.SongId, i.TrackTitle, kind, null, null, false, i.AtUtc, i.Detail))
                .ToList());
    }

    /// <summary>
    /// Groups occurrences into one entry per (album, UTC day).
    /// <para>The day half matters for paging: the feed's cursor is a timestamp, so an entry that spanned
    /// the whole window would straddle every page boundary. Confining an entry to a day keeps each one
    /// on exactly one page (the client still de-dupes on append, belt and braces).</para>
    /// </summary>
    public static IEnumerable<HistorySummary> GroupByAlbumDay(
        string category,
        string kind,
        string tint,
        IEnumerable<ActivityItem> items,
        Func<IReadOnlyList<ActivityItem>, string> headline,
        Func<IReadOnlyList<ActivityItem>, string?>? detail = null)
    {
        foreach (var group in items.GroupBy(i => (Album: i.Album ?? "", Day: i.AtUtc.Date)))
        {
            var rows = group.OrderByDescending(i => i.AtUtc).ToList();
            yield return Summary(
                category, kind, tint,
                // Album name + day, plus the lowest song id in the group so two same-named albums in
                // one day (a rename, a compilation split across folders) still get distinct keys.
                idKey: $"{group.Key.Album}|{group.Key.Day:yyyyMMdd}|{rows.Min(r => r.SongId) ?? 0}",
                headline: headline(rows),
                detail: detail?.Invoke(rows),
                items: rows);
        }
    }
}
