using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Enrichment;

public interface IGeniusLyricsService
{
    /// <summary>
    /// Plain lyrics for the song, or null when Genius has no confident match or could not be reached.
    /// </summary>
    Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default);
}

/// <summary>
/// Reads lyrics from Genius's public pages. The official API needs a token and never returns lyrics, so
/// this uses the site's own search endpoint and song page. Both sit behind Cloudflare, which may refuse
/// a datacenter address; every failure is reported as "nothing". Requests are throttled to
/// <see cref="MusicEnricherOptions.GeniusRequestsPerSecond"/> across every caller, and a run of refusals
/// stops them for a while (<see cref="GeniusRefusalGate"/>).
/// </summary>
public sealed partial class GeniusLyricsService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<GeniusLyricsService> logger,
    TimeProvider time) : IGeniusLyricsService
{
    private const int SearchHitLimit = 5;

    private readonly ReconfigurableRateLimiter _rateLimiter = new();
    private readonly GeniusRefusalGate _refusals = new();

    public async Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist)
            || _refusals.IsClosed(time.GetUtcNow()))
        {
            return null;
        }

        var opts = options.Value;
        var primary = ArtistCreditNormalizer.GetPrimaryArtist(song.Artist) ?? song.Artist;
        var query = $"{primary} {FeaturingSuffix().Replace(song.Title, string.Empty).Trim()}";

        try
        {
            using var search = await SendAsync(
                $"api/search/song?per_page={SearchHitLimit}&q={Uri.EscapeDataString(query)}", ct);
            if (!Succeeded(search, "search", song.Id))
                return null;

            var hits = await ReadHitsAsync(search, ct);
            var hit = GeniusHitMatcher.Pick(
                hits, song.Title, song.Artist, song.Year, opts.IdentityTitleThreshold, opts.IdentityArtistThreshold);
            if (hit is null)
            {
                _refusals.Answered();
                logger.LogDebug("Genius has no confident match for SongId={SongId}", song.Id);
                return null;
            }

            using var page = await SendAsync($"songs/{hit.Id}", ct);
            if (!Succeeded(page, "song page", song.Id))
                return null;

            _refusals.Answered();
            var plain = GeniusLyricsParser.Parse(await page.Content.ReadAsStringAsync(ct));
            if (plain is null)
            {
                logger.LogWarning("Genius song page {GeniusId} for SongId={SongId} held no readable lyrics", hit.Id, song.Id);
                return null;
            }

            return new LyricsResult(null, plain, false, Origin: LyricsOrigin.Genius, GeniusSongId: hit.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Genius lyrics request failed for SongId={SongId}", song.Id);
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(string path, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(options.Value.GeniusRequestsPerSecond, ct);
        if (_refusals.IsClosed(time.GetUtcNow()))
            return null;

        var response = await httpClient.GetAsync(path, ct);
        if (!GeniusRefusalGate.IsRefusal(response.StatusCode))
            return response;

        var now = time.GetUtcNow();
        if (_refusals.Refused(response.StatusCode, RetryAfterHeader.Read(response, now), now) is { } trip)
        {
            logger.LogWarning(
                "Genius refused {Refusals} requests in a row, last with {Status}; not asking it again until {Until}",
                trip.ConsecutiveRefusals, (int)trip.LastStatus, trip.Until);
        }

        return response;
    }

    private bool Succeeded([NotNullWhen(true)] HttpResponseMessage? response, string request, int songId)
    {
        if (response is null)
            return false;
        if (response.IsSuccessStatusCode)
            return true;

        if (GeniusRefusalGate.IsRefusal(response.StatusCode))
            logger.LogDebug("Genius refused the {Request} for SongId={SongId} with {Status}", request, songId, (int)response.StatusCode);
        else
            logger.LogWarning("Genius {Request} returned {Status} for SongId={SongId}", request, (int)response.StatusCode, songId);
        return false;
    }

    private static async Task<IReadOnlyList<GeniusHit>> ReadHitsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var hits = new List<GeniusHit>();
        if (!doc.RootElement.TryGetProperty("response", out var body)
            || !body.TryGetProperty("sections", out var sections))
        {
            return hits;
        }

        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("hits", out var sectionHits))
                continue;

            foreach (var result in sectionHits.EnumerateArray().Select(h => h.GetProperty("result")))
            {
                hits.Add(new GeniusHit(
                    result.GetProperty("id").GetInt32(),
                    result.GetProperty("title").GetString() ?? string.Empty,
                    result.GetProperty("primary_artist").GetProperty("name").GetString() ?? string.Empty,
                    result.TryGetProperty("lyrics_state", out var state) ? state.GetString() : null,
                    result.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True,
                    ReleaseYear(result)));
            }
        }

        return hits;
    }

    private static int? ReleaseYear(JsonElement result) =>
        result.TryGetProperty("release_date_components", out var date)
        && date.ValueKind == JsonValueKind.Object
        && date.TryGetProperty("year", out var year)
        && year.ValueKind == JsonValueKind.Number
        && year.TryGetInt32(out var value)
            ? value
            : null;

    [GeneratedRegex(@"\s*[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s+(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingSuffix();
}
