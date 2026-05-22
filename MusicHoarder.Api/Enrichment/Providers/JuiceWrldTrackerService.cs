using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>A song candidate returned by the community tracker, flattened to the fields we match on.</summary>
public sealed record TrackerSong(
    int Id,
    string Name,
    IReadOnlyList<string> TrackTitles,
    string? Category,
    string? Era,
    string? CreditedArtists,
    string? Producers,
    double? DurationSeconds,
    int? Year);

public interface ITrackerCatalogService
{
    Task<IReadOnlyList<TrackerSong>> SearchAsync(string title, CancellationToken ct = default);
}

/// <summary>
/// Thin client over the community juicewrldapi.com Django REST API — a single-artist Juice WRLD
/// database covering released <i>and</i> unreleased/leaked songs that mainstream catalogs lack.
/// Search hits <c>songs/?search=</c>, which matches across a song's name and its alias
/// <c>track_titles</c>. JSON, no auth; a descriptive User-Agent is set on the injected
/// <see cref="HttpClient"/>. Network/HTTP failures degrade to an empty result so a flaky
/// community endpoint just yields a NoMatch rather than failing the song.
/// </summary>
public sealed partial class JuiceWrldTrackerService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<JuiceWrldTrackerService> logger) : ITrackerCatalogService
{
    public async Task<IReadOnlyList<TrackerSong>> SearchAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var limit = Math.Clamp(options.Value.TrackerSearchLimit, 1, 100);
        var url = $"songs/?format=json&search={Uri.EscapeDataString(title.Trim())}&page_size={limit}";

        try
        {
            using var response = await httpClient.GetAsync(url, ct);
            if ((int)response.StatusCode is 429 or 503)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                logger.LogWarning("Tracker API throttled ({Status}); retry after {Delay}s",
                    (int)response.StatusCode, retryAfter.TotalSeconds);
                throw new ProviderRateLimitedException(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Tracker API HTTP {Status} for search '{Title}'", (int)response.StatusCode, title);
                return [];
            }

            var dto = await response.Content.ReadFromJsonAsync<SearchEnvelope>(cancellationToken: ct);
            if (dto?.Results is null or { Count: 0 })
                return [];

            return dto.Results
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(Map)
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Tracker API request failed for search '{Title}'", title);
            return [];
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    private static TrackerSong Map(SongDto r) => new(
        Id: r.Id,
        Name: r.Name ?? string.Empty,
        TrackTitles: r.TrackTitles?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [],
        Category: r.Category,
        Era: r.Era?.Name,
        CreditedArtists: r.CreditedArtists,
        Producers: r.Producers,
        DurationSeconds: ParseLength(r.Length),
        Year: ParseYear(r.ReleaseDate) ?? ParseYear(r.RecordDates) ?? ParseYear(r.PreviewDate));

    /// <summary>Parses a "m:ss" or "h:mm:ss" duration into seconds; null on anything unexpected.</summary>
    internal static double? ParseLength(string? length)
    {
        if (string.IsNullOrWhiteSpace(length))
            return null;

        var parts = length.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return null;

        var total = 0.0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
                return null;
            total = total * 60 + n;
        }
        return total > 0 ? total : null;
    }

    /// <summary>Best-effort year out of the tracker's free-text date fields (e.g. "Recorded\nJanuary 19, 2016.").</summary>
    internal static int? ParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = YearPattern().Match(text);
        return match.Success && int.TryParse(match.Value, out var y) ? y : null;
    }

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex YearPattern();

    // --- JSON DTOs ---

    private sealed class SearchEnvelope
    {
        [JsonPropertyName("results")]
        public List<SongDto>? Results { get; set; }
    }

    private sealed class SongDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("track_titles")]
        public List<string>? TrackTitles { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("era")]
        public EraDto? Era { get; set; }

        [JsonPropertyName("credited_artists")]
        public string? CreditedArtists { get; set; }

        [JsonPropertyName("producers")]
        public string? Producers { get; set; }

        [JsonPropertyName("length")]
        public string? Length { get; set; }

        [JsonPropertyName("record_dates")]
        public string? RecordDates { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("preview_date")]
        public string? PreviewDate { get; set; }
    }

    private sealed class EraDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
