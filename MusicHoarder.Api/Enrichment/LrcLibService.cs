using System.Net;
using System.Text.Json.Serialization;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public record LyricsResult(
    string? SyncedLyrics,
    string? PlainLyrics,
    bool IsInstrumental,
    int? LrclibId = null);

public interface ILrcLibService
{
    Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default);
}

public sealed class LrcLibService(
    HttpClient httpClient,
    ILogger<LrcLibService> logger) : ILrcLibService
{
    public async Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist))
        {
            logger.LogDebug("Skipping LRCLIB fetch for SongId={SongId}: missing title or artist", song.Id);
            return null;
        }

        var result = await TryExactMatchAsync(song, ct);
        if (result is not null)
        {
            return result;
        }

        return await TrySearchFallbackAsync(song, ct);
    }

    private async Task<LyricsResult?> TryExactMatchAsync(SongMetadata song, CancellationToken ct)
    {
        var url = $"api/get?track_name={Uri.EscapeDataString(song.Title!)}" +
                  $"&artist_name={Uri.EscapeDataString(song.Artist!)}" +
                  (string.IsNullOrWhiteSpace(song.Album) ? string.Empty : $"&album_name={Uri.EscapeDataString(song.Album)}") +
                  (song.DurationSeconds is > 0 ? $"&duration={song.DurationSeconds}" : string.Empty);

        try
        {
            using var response = await httpClient.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("LRCLIB /api/get returned 404 for SongId={SongId}, will try search", song.Id);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LRCLIB /api/get returned {Status} for SongId={SongId}", (int)response.StatusCode, song.Id);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<LrcLibGetResponse>(cancellationToken: ct);
            if (dto is null)
            {
                return null;
            }

            return BuildResult(dto.SyncedLyrics, dto.PlainLyrics, dto.Instrumental, dto.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LRCLIB /api/get request failed for SongId={SongId}", song.Id);
            return null;
        }
    }

    private async Task<LyricsResult?> TrySearchFallbackAsync(SongMetadata song, CancellationToken ct)
    {
        var url = $"api/search?track_name={Uri.EscapeDataString(song.Title!)}" +
                  $"&artist_name={Uri.EscapeDataString(song.Artist!)}";

        try
        {
            using var response = await httpClient.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("LRCLIB /api/search returned {Status} for SongId={SongId}", (int)response.StatusCode, song.Id);
                return null;
            }

            var results = await response.Content.ReadFromJsonAsync<List<LrcLibGetResponse>>(cancellationToken: ct);
            if (results is null or { Count: 0 })
            {
                logger.LogDebug("LRCLIB /api/search returned no results for SongId={SongId}", song.Id);
                return null;
            }

            // Pick the first result that has some lyrics content, preferring synced
            var best = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
                ?? results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PlainLyrics))
                ?? results[0];

            return BuildResult(best.SyncedLyrics, best.PlainLyrics, best.Instrumental, best.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LRCLIB /api/search request failed for SongId={SongId}", song.Id);
            return null;
        }
    }

    private static LyricsResult BuildResult(string? syncedLyrics, string? plainLyrics, bool instrumental, int? lrclibId = null)
    {
        var synced = string.IsNullOrWhiteSpace(syncedLyrics) ? null : syncedLyrics.Trim();
        var plain = string.IsNullOrWhiteSpace(plainLyrics) ? null : plainLyrics.Trim();
        return new LyricsResult(synced, plain, instrumental, lrclibId);
    }

    // --- JSON DTOs ---

    private sealed class LrcLibGetResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; set; }

        [JsonPropertyName("albumName")]
        public string? AlbumName { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
