using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.Enrichment;

public record MusicBrainzRecording(
    string Id,
    string Title,
    string Artist,
    string? AlbumArtist,
    string? ReleaseId,
    string? ReleaseTitle,
    int? Year,
    string? Isrc,
    int? LengthMs,
    int Score = 100,
    int CandidateCount = 1,
    string? Artists = null,
    string? ArtistMusicBrainzIds = null,
    string? AlbumArtistMusicBrainzId = null,
    string? ReleaseGroupId = null,
    string? ReleaseTypePrimary = null,
    string? ReleaseTypes = null,
    bool IsCompilation = false,
    int? TotalDiscs = null,
    int? TotalTracks = null,
    // --- Descriptive metadata (SpotiFLAC-inspired) ---
    string? Genre = null,
    string? ReleaseDate = null,
    string? OriginalReleaseDate = null,
    string? Label = null,
    string? CatalogNumber = null,
    string? Barcode = null,
    string? ArtistSort = null,
    string? AlbumArtistSort = null);

/// <summary>The full canonical tracklist of a single MusicBrainz release (all discs/media flattened).</summary>
public record MusicBrainzRelease(
    string Id,
    string? Title,
    string? AlbumArtist,
    int? Year,
    int? TotalDiscs,
    int? TotalTracks,
    IReadOnlyList<MusicBrainzReleaseTrack> Tracks);

public record MusicBrainzReleaseTrack(
    int DiscNumber,
    int TrackNumber,
    string? Title,
    int? LengthMs,
    string? RecordingId);

/// <summary>A lightweight release-search hit used to resolve a release id from artist + album.</summary>
public record MusicBrainzReleaseSearchResult(
    string Id,
    string? Title,
    int? Year,
    int? TrackCount,
    int Score);

public interface IMusicBrainzWebService
{
    Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default);
    Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default);
    Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(string artist, string title, int limit, string? album = null, CancellationToken ct = default);

    /// <summary>
    /// Free-text recording search (no field qualifiers) for untagged files where a positional
    /// artist/title split is unreliable — lets MusicBrainz's own relevance parse the cleaned filename.
    /// </summary>
    Task<IReadOnlyList<MusicBrainzRecording>> SearchFreeTextAsync(string query, int limit, CancellationToken ct = default);

    /// <summary>Fetches a release's full canonical tracklist by release MBID. Null if not found.</summary>
    Task<MusicBrainzRelease?> LookupReleaseAsync(string releaseId, CancellationToken ct = default);

    /// <summary>Searches releases by artist + album (to resolve a release id when none is stored).</summary>
    Task<IReadOnlyList<MusicBrainzReleaseSearchResult>> SearchReleasesAsync(
        string artist, string album, int limit, CancellationToken ct = default);
}

/// <summary>
/// Thin client over the MusicBrainz web service (musicbrainz.org/ws/2). JSON, rate-limited
/// to honor the 1 request/second policy via a shared token bucket. A descriptive User-Agent
/// is required by MusicBrainz; it is set on the injected <see cref="HttpClient"/>.
/// Response payloads are turned into domain records by <see cref="MusicBrainzResponseMapper"/>.
/// </summary>
public sealed class MusicBrainzWebService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<MusicBrainzWebService> logger) : IMusicBrainzWebService
{
    private static readonly ReconfigurableRateLimiter RateLimiter = new();

    public async Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default)
    {
        var dto = await GetAsync<MusicBrainzRecordingDto>(
            $"recording/{Uri.EscapeDataString(mbid)}?inc=artist-credits+releases+release-groups+media+isrcs+genres+labels&fmt=json", ct);
        return dto is null ? null : MusicBrainzResponseMapper.MapRecording(dto);
    }

    public async Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
    {
        var normalized = isrc.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
        var dto = await GetAsync<MusicBrainzIsrcDto>(
            $"isrc/{Uri.EscapeDataString(normalized)}?inc=artist-credits+releases+release-groups+media+genres+labels&fmt=json", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return null;

        var count = dto.Recordings.Count;
        return MusicBrainzResponseMapper.MapRecording(dto.Recordings[0]) with { CandidateCount = count, Isrc = normalized };
    }

    public async Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(
        string artist, string title, int limit, string? album = null, CancellationToken ct = default)
    {
        var query = $"artist:\"{EscapeLucene(artist)}\" AND recording:\"{EscapeLucene(title)}\"";
        if (!string.IsNullOrWhiteSpace(album))
            query += $" AND release:\"{EscapeLucene(album)}\"";
        var dto = await GetAsync<MusicBrainzRecordingSearchDto>(
            $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return [];

        return dto.Recordings.Select(MusicBrainzResponseMapper.MapRecording).ToList();
    }

    public async Task<IReadOnlyList<MusicBrainzRecording>> SearchFreeTextAsync(
        string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var dto = await GetAsync<MusicBrainzRecordingSearchDto>(
            $"recording?query={Uri.EscapeDataString(EscapeLucene(query))}&fmt=json&limit={limit}", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return [];

        return dto.Recordings.Select(MusicBrainzResponseMapper.MapRecording).ToList();
    }

    public async Task<MusicBrainzRelease?> LookupReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        var dto = await GetAsync<MusicBrainzReleaseDetailDto>(
            $"release/{Uri.EscapeDataString(releaseId)}?inc=artist-credits+recordings+media&fmt=json", ct);
        return dto is null ? null : MusicBrainzResponseMapper.MapRelease(dto);
    }

    public async Task<IReadOnlyList<MusicBrainzReleaseSearchResult>> SearchReleasesAsync(
        string artist, string album, int limit, CancellationToken ct = default)
    {
        var query = $"artist:\"{EscapeLucene(artist)}\" AND release:\"{EscapeLucene(album)}\"";
        var dto = await GetAsync<MusicBrainzReleaseSearchDto>(
            $"release?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}", ct);
        return MusicBrainzResponseMapper.MapReleaseSearchResults(dto?.Releases);
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct) where T : class
    {
        using var lease = await RateLimiter.AcquireAsync(Math.Max(1, options.Value.MusicBrainzRequestsPerSecond), ct);
        if (!lease.IsAcquired)
        {
            logger.LogWarning("MusicBrainz rate limiter rejected the request");
            return null;
        }

        try
        {
            using var response = await httpClient.GetAsync(relativeUrl, ct);
            if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                logger.LogWarning("MusicBrainz throttled ({Status}); retry after {Delay}s",
                    (int)response.StatusCode, retryAfter.TotalSeconds);
                throw new ProviderRateLimitedException(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("MusicBrainz HTTP {Status} for {Url}", (int)response.StatusCode, relativeUrl);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "MusicBrainz request failed for {Url}", relativeUrl);
            return null;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    private static string EscapeLucene(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
