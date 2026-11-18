using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public interface IGeniusLyricsService
{
    /// <summary>
    /// Plain lyrics for the song, or null when Genius has no confident match or could not be reached.
    /// Both come back as null on purpose: an unreachable Genius says nothing about the song.
    /// </summary>
    Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default);
}

/// <summary>
/// Reads lyrics from Genius's public pages. The official API needs a token and never returns lyrics, so
/// this uses the site's own search endpoint and song page. Both sit behind Cloudflare, which may answer
/// 403 to a datacenter address; every such failure is logged and reported as "nothing".
/// </summary>
public sealed partial class GeniusLyricsService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<GeniusLyricsService> logger) : IGeniusLyricsService
{
    private const int SearchHitLimit = 5;

    public async Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist))
            return null;

        var opts = options.Value;
        var primary = ArtistCreditNormalizer.GetPrimaryArtist(song.Artist) ?? song.Artist;
        var query = $"{primary} {FeaturingSuffix().Replace(song.Title, string.Empty).Trim()}";

        try
        {
            var hits = await SearchAsync(query, ct);
            var hit = GeniusHitMatcher.Pick(
                hits, song.Title, song.Artist, opts.IdentityTitleThreshold, opts.IdentityArtistThreshold);
            if (hit is null)
            {
                logger.LogDebug("Genius has no confident match for SongId={SongId}", song.Id);
                return null;
            }

            using var response = await httpClient.GetAsync($"songs/{hit.Id}", ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Genius song page returned {Status} for SongId={SongId}", (int)response.StatusCode, song.Id);
                return null;
            }

            var plain = GeniusLyricsParser.Parse(await response.Content.ReadAsStringAsync(ct));
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

    private async Task<IReadOnlyList<GeniusHit>> SearchAsync(string query, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(
            $"api/search/song?per_page={SearchHitLimit}&q={Uri.EscapeDataString(query)}", ct);
        response.EnsureSuccessStatusCode();

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
                    result.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True));
            }
        }

        return hits;
    }

    [GeneratedRegex(@"\s*[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s+(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingSuffix();
}
