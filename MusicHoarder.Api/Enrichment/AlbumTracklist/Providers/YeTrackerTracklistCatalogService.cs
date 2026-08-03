using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;

/// <summary>One album's running order as documented by the community tracker.</summary>
/// <param name="Legibility">How readable the source image was ("Clear", "Blurry", "Blocked").</param>
/// <param name="IsSetlist">
/// True when the row documents a concert setlist rather than an album. Kept in the catalog (it is
/// still a real tracklist) but never offered as an album's running order.
/// </param>
public sealed record TrackerTracklist(
    string Album,
    string? Era,
    int? Year,
    string? Legibility,
    IReadOnlyList<TrackerTracklistEntry> Tracks,
    bool IsSetlist = false);

public sealed record TrackerTracklistEntry(int Number, string Title);

/// <summary>
/// Local, in-memory catalog of the yetracker's Tracklists tab, normalized offline (see
/// <c>tools/yetracker-import</c>) into a committed <c>Data/yetracker-tracklists.json</c> and loaded
/// once at startup.
/// <para>
/// This is the only tracklist source that covers albums which were never released — Yandhi, Good
/// Ass Job, the various DONDA cuts — where MusicBrainz, Spotify, Deezer and Apple Music all have
/// nothing at all. Concert setlists share the tab and are dropped on load: they record what was
/// performed on a night, which is not an album's running order.
/// </para>
/// </summary>
public sealed class YeTrackerTracklistCatalogService
{
    private readonly IReadOnlyList<TrackerTracklist> _tracklists;

    public YeTrackerTracklistCatalogService(ILogger<YeTrackerTracklistCatalogService> logger)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "yetracker-tracklists.json");
        _tracklists = Load(path, logger);
    }

    /// <summary>Test seam: build directly from in-memory tracklists (no disk load).</summary>
    internal YeTrackerTracklistCatalogService(IEnumerable<TrackerTracklist> tracklists)
        => _tracklists = tracklists.ToList();

    /// <summary>
    /// The best tracklist for an album name, or null. Matches the album name exactly (normalized),
    /// then falls back to the era — leaked tracks are usually filed under the era, and for an
    /// unreleased project the era and the album are the same thing.
    /// </summary>
    public TrackerTracklist? Find(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
            return null;

        var key = Normalize(album);
        if (key.Length == 0)
            return null;

        TrackerTracklist? byEra = null;
        foreach (var tracklist in _tracklists)
        {
            // A setlist records what was performed on a night — a real tracklist, but not this
            // album's running order, so it must never stand in for one.
            if (tracklist.IsSetlist || tracklist.Tracks.Count < 2)
                continue;
            if (Normalize(tracklist.Album) == key)
                return tracklist;
            if (byEra is null && tracklist.Era is { Length: > 0 } era && Normalize(era) == key)
                byEra = tracklist;
        }
        return byEra;
    }

    private static IReadOnlyList<TrackerTracklist> Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning(
                "yetracker tracklist catalog not found at {Path}; the tracklist provider will return no albums", path);
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            var rows = JsonSerializer.Deserialize<List<CatalogRow>>(stream) ?? [];
            var result = new List<TrackerTracklist>(rows.Count);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Album) || row.Tracks is null or { Count: < 2 })
                    continue;

                var tracks = row.Tracks
                    .Where(t => t.Number > 0 && !string.IsNullOrWhiteSpace(t.Title))
                    .Select(t => new TrackerTracklistEntry(t.Number, t.Title!))
                    .ToList();
                if (tracks.Count < 2)
                    continue;

                result.Add(new TrackerTracklist(
                    row.Album!, row.Era, row.Year, row.Legibility, tracks, row.IsSetlist));
            }

            logger.LogInformation("Loaded {Count} yetracker tracklists from {Path}", result.Count, path);
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse yetracker tracklists at {Path}; the provider is disabled", path);
            return [];
        }
    }

    private static string Normalize(string s)
    {
        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        var n = 0;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
                buffer[n++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..n]);
    }

    private sealed class CatalogRow
    {
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("era")] public string? Era { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("legibility")] public string? Legibility { get; set; }
        [JsonPropertyName("isSetlist")] public bool IsSetlist { get; set; }
        [JsonPropertyName("tracks")] public List<TrackRow>? Tracks { get; set; }
    }

    private sealed class TrackRow
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }
}
