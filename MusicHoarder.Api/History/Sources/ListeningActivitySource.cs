using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// What the owner did with the music rather than to it: likes (whether tapped here or mirrored in from
/// a Spotify save) and the playlists exported to disk.
/// <para>
/// Plays are deliberately absent. They are the one genuinely high-volume signal in the app and would
/// bury every other category; the Overview page is where listening belongs.
/// </para>
/// </summary>
public sealed class ListeningActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var liked = await db.Songs
            .AsNoTracking()
            .Where(s => s.LikedAtUtc != null
                && s.LikedAtUtc >= window.FromUtc && s.LikedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.LikedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist,
                At = s.LikedAtUtc!.Value,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        // Grouped per day rather than per album: a like is about the track, and a day's likes read as
        // one thing ("you liked 9 songs") far better than nine near-identical rows.
        foreach (var group in liked.GroupBy(l => l.At.Date))
        {
            var rows = group.OrderByDescending(l => l.At).ToList();
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Listening, "liked", ActivityTint.Ok,
                idKey: $"{group.Key:yyyyMMdd}|{rows.Min(r => r.Id)}",
                headline: rows.Count == 1
                    ? $"Liked {ActivityText.Quote(rows[0].Title)}"
                    : $"Liked {ActivityText.Plural(rows.Count, "track")}",
                detail: rows.Count == 1
                    ? rows[0].AlbumArtist ?? rows[0].Artist
                    : NameThem(rows.Select(r => r.Title)),
                items: rows.Select(r => new ActivityItem(
                    r.Id, r.Title, r.Album, r.AlbumArtist ?? r.Artist, r.At)).ToList()));
        }

        var playlists = await db.ExportedPlaylists
            .AsNoTracking()
            .Where(p => p.LastGeneratedAtUtc != null
                && p.LastGeneratedAtUtc >= window.FromUtc && p.LastGeneratedAtUtc <= window.ToUtc)
            .OrderByDescending(p => p.LastGeneratedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(p => new
            {
                p.Id, p.Name, p.MatchedTrackCount, p.SpotifyTrackTotal,
                At = p.LastGeneratedAtUtc!.Value,
            })
            .ToListAsync(ct);

        summaries.AddRange(playlists.Select(p => ActivityText.Summary(
            ActivityCategory.Listening, "playlist-exported", ActivityTint.Info,
            idKey: p.Id.ToString(),
            headline: $"Wrote the playlist {ActivityText.Quote(p.Name)} to the library",
            detail: p.SpotifyTrackTotal > 0
                ? $"{p.MatchedTrackCount} of {p.SpotifyTrackTotal} tracks are in your library"
                : null,
            items: [new ActivityItem(null, p.Name, null, null, p.At)])));

        return summaries;
    }

    /// <summary>"Nights, Ivy and 4 more" — a day's likes span albums, so the tracks are the subtitle.</summary>
    private static string NameThem(IEnumerable<string?> titles)
    {
        var named = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        if (named.Count == 0) return "";
        if (named.Count <= 3) return string.Join(", ", named);
        return $"{string.Join(", ", named.Take(2))} and {named.Count - 2} more";
    }
}
