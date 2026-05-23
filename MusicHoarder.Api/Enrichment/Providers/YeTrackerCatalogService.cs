using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Local, in-memory catalog for the Kanye West "yetracker" community tracker. Unlike the Juice WRLD
/// tracker there is no live API, so the scraped tracker is normalized offline (see
/// <c>tools/yetracker-import</c>) into a committed <c>Data/yetracker.json</c> that this service loads
/// once at startup. <see cref="SearchAsync"/> is a coarse case-insensitive contains filter over the
/// song name and its aliases (mirroring the server-side <c>search=</c> the Juice WRLD API does) —
/// the precise fuzzy scoring still happens in <see cref="CommunityTrackerEnrichmentProvider"/>.
/// </summary>
public sealed class YeTrackerCatalogService : ITrackerCatalogService
{
    private const string CreditedArtist = "Kanye West";

    private readonly IOptions<MusicEnricherOptions> _options;
    private readonly IReadOnlyList<Entry> _entries;

    public YeTrackerCatalogService(IOptions<MusicEnricherOptions> options, ILogger<YeTrackerCatalogService> logger)
    {
        _options = options;
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "yetracker.json");
        _entries = Load(path, logger);
    }

    /// <summary>Test seam: build directly from in-memory songs (no disk load).</summary>
    internal YeTrackerCatalogService(IEnumerable<TrackerSong> songs, IOptions<MusicEnricherOptions> options)
    {
        _options = options;
        _entries = songs.Select(Entry.From).ToList();
    }

    public Task<IReadOnlyList<TrackerSong>> SearchAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult<IReadOnlyList<TrackerSong>>([]);

        var query = Normalize(title);
        if (query.Length == 0)
            return Task.FromResult<IReadOnlyList<TrackerSong>>([]);

        var limit = Math.Clamp(_options.Value.TrackerSearchLimit, 1, 100);

        var results = _entries
            .Where(e => e.Matches(query))
            .OrderBy(e => Math.Abs(e.PrimaryKey.Length - query.Length))
            .Take(limit)
            .Select(e => e.Song)
            .ToList();

        return Task.FromResult<IReadOnlyList<TrackerSong>>(results);
    }

    private static IReadOnlyList<Entry> Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning("yetracker catalog not found at {Path}; YeTracker provider will return no matches", path);
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            var rows = JsonSerializer.Deserialize<List<CatalogRow>>(stream) ?? [];
            var entries = new List<Entry>(rows.Count);
            var id = 0;
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Title))
                    continue;
                entries.Add(Entry.From(new TrackerSong(
                    Id: ++id,
                    Name: row.Title!,
                    TrackTitles: row.AltTitles ?? [],
                    Category: row.Category,
                    Era: row.Era,
                    CreditedArtists: CreditedArtist,
                    Producers: row.Producers,
                    DurationSeconds: row.DurationSeconds,
                    Year: row.Year)));
            }

            logger.LogInformation("Loaded {Count} yetracker songs from {Path}", entries.Count, path);
            return entries;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse yetracker catalog at {Path}; YeTracker provider disabled", path);
            return [];
        }
    }

    /// <summary>Lowercase, letters+digits only — used for the coarse contains filter.</summary>
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

    private sealed record Entry(TrackerSong Song, string PrimaryKey, string[] Keys)
    {
        public static Entry From(TrackerSong song)
        {
            var keys = new List<string> { Normalize(song.Name) };
            foreach (var alias in song.TrackTitles)
            {
                var k = Normalize(alias);
                if (k.Length > 0 && !keys.Contains(k))
                    keys.Add(k);
            }
            return new Entry(song, keys[0], [.. keys]);
        }

        public bool Matches(string query)
        {
            foreach (var key in Keys)
            {
                if (key.Length == 0)
                    continue;
                if (key.Contains(query, StringComparison.Ordinal) || query.Contains(key, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    private sealed class CatalogRow
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("altTitles")] public List<string>? AltTitles { get; set; }
        [JsonPropertyName("era")] public string? Era { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("producers")] public string? Producers { get; set; }
        [JsonPropertyName("durationSeconds")] public double? DurationSeconds { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
    }
}
