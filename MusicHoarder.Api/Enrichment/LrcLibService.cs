using System.Net;
using System.Text.Json.Serialization;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <param name="DurationSeconds">
/// The track length LRCLIB holds for the matched entry. Carried through so the caller can persist it: a
/// disagreement with our own duration is the cheapest and most decisive sign the LRC was timed against a
/// different recording of the song (see <see cref="LyricsTimingValidator"/>).
/// </param>
public record LyricsResult(
    string? SyncedLyrics,
    string? PlainLyrics,
    bool IsInstrumental,
    int? LrclibId = null,
    double? DurationSeconds = null);

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

        // LRCLIB indexes one primary artist per track, so a combined credit
        // ("Fenix Flexin, Purps On The Beat", "A feat. B") matches nothing on either
        // /api/get or /api/search. Try the stored credit first (covers solo artists and
        // records LRCLIB happens to store under the exact credit), then fall back to the
        // primary artist. Exact match is preferred over search across both candidates.
        var artistCandidates = new List<string> { song.Artist! };
        var primary = ArtistCreditNormalizer.GetPrimaryArtist(song.Artist);
        if (!string.IsNullOrWhiteSpace(primary)
            && !primary.Equals(song.Artist, StringComparison.OrdinalIgnoreCase))
        {
            artistCandidates.Add(primary);
        }

        foreach (var artist in artistCandidates)
        {
            var result = await TryExactMatchAsync(song, artist, ct);
            if (result is not null)
            {
                return result;
            }
        }

        foreach (var artist in artistCandidates)
        {
            var result = await TrySearchFallbackAsync(song, artist, ct);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<LyricsResult?> TryExactMatchAsync(SongMetadata song, string artistName, CancellationToken ct)
    {
        var url = $"api/get?track_name={Uri.EscapeDataString(song.Title!)}" +
                  $"&artist_name={Uri.EscapeDataString(artistName)}" +
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

            return BuildResult(dto.SyncedLyrics, dto.PlainLyrics, dto.Instrumental, dto.Id, dto.Duration);
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

    private async Task<LyricsResult?> TrySearchFallbackAsync(SongMetadata song, string artistName, CancellationToken ct)
    {
        var url = $"api/search?track_name={Uri.EscapeDataString(song.Title!)}" +
                  $"&artist_name={Uri.EscapeDataString(artistName)}";

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

            // Unlike /api/get — which we hand a &duration= and which enforces a match — /api/search is keyed
            // on track name alone, so it happily returns a LIVE cut, a sped-up edit or an extended mix of the
            // same song. Those carry the right words on a clock that has nothing to do with our audio, which
            // is the single biggest source of "the timestamps are very off". Drop entries whose length
            // disagrees with ours before picking, and only fall back to the unfiltered list when we have no
            // duration of our own to compare against.
            var candidates = results;
            if (song.DurationSeconds is > 0)
            {
                var ours = (double)song.DurationSeconds.Value;
                var timed = results.Where(r => r.Duration > 0).ToList();
                var sameLength = timed
                    .Where(r => Math.Abs(r.Duration - ours) <= LyricsTimingValidator.DurationToleranceSeconds)
                    .ToList();

                if (sameLength.Count > 0)
                {
                    // Closest length first, so a re-recording that squeaks inside the tolerance still loses
                    // to an exact match.
                    candidates = sameLength.OrderBy(r => Math.Abs(r.Duration - ours)).ToList();
                }
                else if (timed.Count > 0)
                {
                    // Every entry declared a length and every one of them disagrees with ours. That is
                    // evidence, not absence of it: these lyrics were timed against a recording we do not
                    // hold, so taking them would mean shipping timestamps we already know are wrong.
                    logger.LogDebug(
                        "LRCLIB /api/search returned {Count} result(s) for SongId={SongId}, none within {Tolerance}s of our {Duration}s track; skipping.",
                        results.Count, song.Id, LyricsTimingValidator.DurationToleranceSeconds, ours);
                    return null;
                }

                // Otherwise no entry declared a length at all — we have nothing to judge on, so fall through
                // unfiltered rather than discarding a hit over a field the response happened to omit. The
                // timing check downstream still gets its say once the lyrics are stored.
            }

            // Pick the first result that has some lyrics content, preferring synced
            var best = candidates.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
                ?? candidates.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PlainLyrics))
                ?? candidates[0];

            return BuildResult(best.SyncedLyrics, best.PlainLyrics, best.Instrumental, best.Id, best.Duration);
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

    private static LyricsResult BuildResult(
        string? syncedLyrics, string? plainLyrics, bool instrumental, int? lrclibId = null, double duration = 0)
    {
        var synced = string.IsNullOrWhiteSpace(syncedLyrics) ? null : syncedLyrics.Trim();
        var plain = string.IsNullOrWhiteSpace(plainLyrics) ? null : plainLyrics.Trim();
        return new LyricsResult(synced, plain, instrumental, lrclibId, duration > 0 ? duration : null);
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
