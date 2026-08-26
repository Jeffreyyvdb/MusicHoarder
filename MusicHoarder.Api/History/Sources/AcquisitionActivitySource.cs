using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// Music arriving. Covers every way a track can reach the library — a scan finding a file on the
/// share, the downloader fetching a wishlisted track, album completion filling in the tracks nobody
/// asked for, and a quality upgrade replacing a copy with a better one — plus the wishlist churn and
/// download failures behind them.
/// </summary>
public sealed class AcquisitionActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var summaries = new List<HistorySummary>();
        summaries.AddRange(await CollectNewTracksAsync(window, ct));
        summaries.AddRange(await CollectWishlistAsync(window, ct));
        summaries.AddRange(await CollectUpgradesAsync(window, ct));
        summaries.AddRange(await CollectAlbumCompletionAsync(window, ct));
        return summaries;
    }

    /// <summary>
    /// Tracks that entered the library in the window, split by <b>how</b> they got here. The wishlist
    /// link is the "why" (the same fact <see cref="Library.SongOriginResolver"/> derives for the
    /// library's Source column); a track with no link was simply found on the source share.
    /// </summary>
    private async Task<List<HistorySummary>> CollectNewTracksAsync(ActivityWindow window, CancellationToken ct)
    {
        var added = await db.Songs
            .AsNoTracking()
            .Where(s => s.AcquiredAtUtc != null
                && s.AcquiredAtUtc >= window.FromUtc && s.AcquiredAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.AcquiredAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist,
                At = s.AcquiredAtUtc!.Value,
            })
            .ToListAsync(ct);

        if (added.Count == 0) return [];

        // One query for the "why": the wishlist rows that produced these songs.
        var ids = added.Select(a => a.Id).ToList();
        var links = await db.WishlistItems
            .AsNoTracking()
            .Where(w => w.DownloadedSongId != null && ids.Contains(w.DownloadedSongId!.Value))
            .Select(w => new
            {
                SongId = w.DownloadedSongId!.Value,
                w.Origin,
                w.DownloadProvider,
                SourceType = (WishlistSourceType?)(w.WishlistSource != null ? w.WishlistSource.SourceType : null),
                SourceName = w.WishlistSource != null ? w.WishlistSource.Name : null,
                w.SourceUrl,
            })
            .ToListAsync(ct);
        var linkBySong = links
            .GroupBy(l => l.SongId)
            .ToDictionary(g => g.Key, g => g.First());

        var summaries = new List<HistorySummary>();
        foreach (var originGroup in added.GroupBy(a =>
            linkBySong.TryGetValue(a.Id, out var link) ? Describe(link.Origin, link.SourceType, link.SourceName, link.SourceUrl) : ScannedOrigin))
        {
            var (kind, verb, detail) = originGroup.Key;
            summaries.AddRange(ActivityText.GroupByAlbumDay(
                ActivityCategory.Acquired, kind, ActivityTint.Ok,
                originGroup.Select(a => new ActivityItem(a.Id, a.Title, a.Album, a.AlbumArtist ?? a.Artist, a.At, detail)),
                rows => $"{verb} {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
                _ => detail));
        }
        return summaries;
    }

    private static readonly (string Kind, string Verb, string? Detail) ScannedOrigin =
        ("scanned-in", "Found", "Already on the source share — picked up by a scan");

    private static (string Kind, string Verb, string? Detail) Describe(
        WishlistItemOrigin origin, WishlistSourceType? sourceType, string? sourceName, string? sourceUrl)
    {
        if (origin == WishlistItemOrigin.AlbumCompletion)
            return ("album-filled", "Filled in", "Completed the album — nobody asked for these individually");

        return sourceType switch
        {
            WishlistSourceType.LikedSongs => ("downloaded", "Downloaded", $"From Spotify — {sourceName ?? "Liked Songs"}"),
            WishlistSourceType.Playlist => ("downloaded", "Downloaded", $"From the Spotify playlist {ActivityText.Quote(sourceName)}"),
            WishlistSourceType.DeezerPlaylist => ("downloaded", "Downloaded", $"From the Deezer playlist {ActivityText.Quote(sourceName)}"),
            _ => ("downloaded", "Downloaded", sourceUrl is null ? "Added by hand" : $"Added by hand from {HostOf(sourceUrl)}"),
        };
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>Wishlist churn: tracks queued for download, and the ones the downloader could not get.</summary>
    private async Task<List<HistorySummary>> CollectWishlistAsync(ActivityWindow window, CancellationToken ct)
    {
        var items = await db.WishlistItems
            .AsNoTracking()
            .Where(w => (w.CreatedAtUtc >= window.FromUtc && w.CreatedAtUtc <= window.ToUtc)
                || (w.UpdatedAtUtc >= window.FromUtc && w.UpdatedAtUtc <= window.ToUtc
                    && (w.Status == WishlistItemStatus.Failed || w.Status == WishlistItemStatus.NotFound)))
            .OrderByDescending(w => w.UpdatedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(w => new
            {
                w.Id, w.Title, w.Artist, w.Album, w.Status, w.Origin, w.CreatedAtUtc, w.UpdatedAtUtc, w.LastError,
                SourceName = w.WishlistSource != null ? w.WishlistSource.Name : null,
                SourceType = (WishlistSourceType?)(w.WishlistSource != null ? w.WishlistSource.SourceType : null),
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        // Queued: grouped by the collection that pulled them in, per day. Album completion is excluded —
        // its own entry (below) says it better than "42 tracks added to the wishlist" would.
        var queued = items
            .Where(w => window.Covers(w.CreatedAtUtc) && w.Origin != WishlistItemOrigin.AlbumCompletion)
            .ToList();
        foreach (var group in queued.GroupBy(w => (w.SourceType, w.SourceName, Day: w.CreatedAtUtc.Date)))
        {
            var rows = group.OrderByDescending(w => w.CreatedAtUtc).ToList();
            var where = group.Key.SourceType switch
            {
                WishlistSourceType.LikedSongs => $"from Spotify — {group.Key.SourceName ?? "Liked Songs"}",
                WishlistSourceType.Playlist => $"from the Spotify playlist {ActivityText.Quote(group.Key.SourceName)}",
                WishlistSourceType.DeezerPlaylist => $"from the Deezer playlist {ActivityText.Quote(group.Key.SourceName)}",
                _ => "by hand",
            };
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Acquired, "wishlist-added", ActivityTint.Info,
                idKey: $"{group.Key.SourceType}|{group.Key.SourceName}|{group.Key.Day:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: $"Wishlisted {ActivityText.Plural(rows.Count, "track")} {where}",
                detail: rows.Count <= 3 ? string.Join(", ", rows.Select(r => $"{r.Artist} — {r.Title}")) : null,
                items: rows.Select(r => new ActivityItem(
                    null, $"{r.Artist} — {r.Title}", r.Album, r.Artist, r.CreatedAtUtc)).ToList()));
        }

        var failed = items
            .Where(w => window.Covers(w.UpdatedAtUtc)
                && w.Status is WishlistItemStatus.Failed or WishlistItemStatus.NotFound)
            .ToList();
        foreach (var group in failed.GroupBy(w => (w.Status, Day: w.UpdatedAtUtc.Date)))
        {
            var rows = group.OrderByDescending(w => w.UpdatedAtUtc).ToList();
            var headline = group.Key.Status == WishlistItemStatus.NotFound
                ? $"Could not find {ActivityText.Plural(rows.Count, "wishlisted track")} anywhere"
                : $"Download failed for {ActivityText.Plural(rows.Count, "wishlisted track")}";
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Acquired,
                group.Key.Status == WishlistItemStatus.NotFound ? "download-not-found" : "download-failed",
                group.Key.Status == WishlistItemStatus.NotFound ? ActivityTint.Warn : ActivityTint.Error,
                idKey: $"{group.Key.Status}|{group.Key.Day:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: headline,
                detail: ActivityText.Clip(rows[0].LastError),
                items: rows.Select(r => new ActivityItem(
                    null, $"{r.Artist} — {r.Title}", r.Album, r.Artist, r.UpdatedAtUtc, ActivityText.Clip(r.LastError))).ToList()));
        }

        return summaries;
    }

    /// <summary>Quality upgrades: a better copy found and swapped in, or the search coming up empty.</summary>
    private async Task<List<HistorySummary>> CollectUpgradesAsync(ActivityWindow window, CancellationToken ct)
    {
        var upgrades = await db.UpgradeRequests
            .AsNoTracking()
            .Where(u => u.CompletedAtUtc != null
                && u.CompletedAtUtc >= window.FromUtc && u.CompletedAtUtc <= window.ToUtc
                && u.Status != UpgradeRequestStatus.Cancelled)
            .OrderByDescending(u => u.CompletedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(u => new
            {
                u.Id, u.SongId, u.Status, u.Trigger, u.Error, u.CandidateQualityScore,
                At = u.CompletedAtUtc!.Value,
                Title = u.Song != null ? u.Song.Title : null,
                Album = u.Song != null ? u.Song.Album : null,
                AlbumArtist = u.Song != null ? u.Song.AlbumArtist : null,
                Artist = u.Song != null ? u.Song.Artist : null,
                Codec = u.Song != null ? u.Song.Extension : null,
                Bitrate = u.Song != null ? u.Song.Bitrate : null,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();
        foreach (var u in upgrades.Where(u => u.Status == UpgradeRequestStatus.Completed))
        {
            var quality = string.Join(" ", new[]
            {
                u.Codec?.TrimStart('.').ToUpperInvariant(),
                u.Bitrate is { } kbps and > 0 ? $"{kbps} kbps" : null,
            }.Where(p => p is not null));
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Acquired, "upgrade-applied", ActivityTint.Ok,
                idKey: u.Id.ToString(),
                headline: $"Upgraded {ActivityText.Quote(u.Title)} to a better copy",
                detail: string.IsNullOrWhiteSpace(quality)
                    ? (u.Trigger == UpgradeTrigger.Auto ? "Found automatically" : "You asked for this one")
                    : $"Now {quality}{(u.Trigger == UpgradeTrigger.Auto ? " — found automatically" : "")}",
                items: [new ActivityItem(u.SongId, u.Title, u.Album, u.AlbumArtist ?? u.Artist, u.At)]));
        }

        foreach (var group in upgrades
            .Where(u => u.Status is UpgradeRequestStatus.NotFound or UpgradeRequestStatus.Failed)
            .GroupBy(u => (u.Status, Day: u.At.Date)))
        {
            var rows = group.OrderByDescending(u => u.At).ToList();
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Acquired,
                group.Key.Status == UpgradeRequestStatus.NotFound ? "upgrade-not-found" : "upgrade-failed",
                group.Key.Status == UpgradeRequestStatus.NotFound ? ActivityTint.Info : ActivityTint.Warn,
                idKey: $"{group.Key.Status}|{group.Key.Day:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: group.Key.Status == UpgradeRequestStatus.NotFound
                    ? $"No better copy exists for {ActivityText.Plural(rows.Count, "track")}"
                    : $"Upgrade failed for {ActivityText.Plural(rows.Count, "track")}",
                detail: ActivityText.Clip(rows[0].Error),
                items: rows.Select(r => new ActivityItem(
                    r.SongId, r.Title, r.Album, r.AlbumArtist ?? r.Artist, r.At, ActivityText.Clip(r.Error))).ToList()));
        }

        return summaries;
    }

    /// <summary>Album completion deciding an album was short and queueing the missing tracks.</summary>
    private async Task<List<HistorySummary>> CollectAlbumCompletionAsync(ActivityWindow window, CancellationToken ct)
    {
        var filled = await db.AlbumCompletionStates
            .AsNoTracking()
            .Where(a => a.Status == AlbumCompletionStatus.Filled
                && a.EnqueuedTrackCount > 0
                && a.UpdatedAtUtc >= window.FromUtc && a.UpdatedAtUtc <= window.ToUtc)
            .OrderByDescending(a => a.UpdatedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(a => new
            {
                a.Id, a.UpdatedAtUtc, a.EnqueuedTrackCount, a.OwnedTrackCount, a.CanonicalTrackCount,
                Album = a.CanonicalAlbum.DisplayTitle,
                Artist = a.CanonicalAlbum.DisplayArtist,
            })
            .ToListAsync(ct);

        return filled.Select(a => ActivityText.Summary(
            ActivityCategory.Acquired, "album-completion", ActivityTint.Info,
            idKey: a.Id.ToString(),
            headline: $"Went looking for {ActivityText.Plural(a.EnqueuedTrackCount, "missing track")} of {ActivityText.Quote(a.Album)}",
            detail: $"You had {a.OwnedTrackCount} of {a.CanonicalTrackCount}",
            items: [new ActivityItem(null, null, a.Album, a.Artist, a.UpdatedAtUtc)])).ToList();
    }
}
