using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
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
    int? TotalTracks = null);

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
/// </summary>
public sealed class MusicBrainzWebService(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<MusicBrainzWebService> logger) : IMusicBrainzWebService
{
    private static readonly ReconfigurableRateLimiter RateLimiter = new();

    public async Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default)
    {
        var dto = await GetAsync<RecordingDto>(
            $"recording/{Uri.EscapeDataString(mbid)}?inc=artist-credits+releases+release-groups+media+isrcs&fmt=json", ct);
        return dto is null ? null : MapRecording(dto);
    }

    public async Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
    {
        var normalized = isrc.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
        var dto = await GetAsync<IsrcDto>(
            $"isrc/{Uri.EscapeDataString(normalized)}?inc=artist-credits+releases+release-groups+media&fmt=json", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return null;

        var count = dto.Recordings.Count;
        return MapRecording(dto.Recordings[0]) with { CandidateCount = count, Isrc = normalized };
    }

    public async Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(
        string artist, string title, int limit, string? album = null, CancellationToken ct = default)
    {
        var query = $"artist:\"{EscapeLucene(artist)}\" AND recording:\"{EscapeLucene(title)}\"";
        if (!string.IsNullOrWhiteSpace(album))
            query += $" AND release:\"{EscapeLucene(album)}\"";
        var dto = await GetAsync<SearchDto>(
            $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return [];

        return dto.Recordings.Select(MapRecording).ToList();
    }

    public async Task<IReadOnlyList<MusicBrainzRecording>> SearchFreeTextAsync(
        string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var dto = await GetAsync<SearchDto>(
            $"recording?query={Uri.EscapeDataString(EscapeLucene(query))}&fmt=json&limit={limit}", ct);
        if (dto?.Recordings is null or { Count: 0 })
            return [];

        return dto.Recordings.Select(MapRecording).ToList();
    }

    public async Task<MusicBrainzRelease?> LookupReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        var dto = await GetAsync<ReleaseDetailDto>(
            $"release/{Uri.EscapeDataString(releaseId)}?inc=artist-credits+recordings+media&fmt=json", ct);
        return dto is null ? null : MapRelease(dto);
    }

    public async Task<IReadOnlyList<MusicBrainzReleaseSearchResult>> SearchReleasesAsync(
        string artist, string album, int limit, CancellationToken ct = default)
    {
        var query = $"artist:\"{EscapeLucene(artist)}\" AND release:\"{EscapeLucene(album)}\"";
        var dto = await GetAsync<ReleaseSearchDto>(
            $"release?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}", ct);
        if (dto?.Releases is null or { Count: 0 })
            return [];

        return dto.Releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => new MusicBrainzReleaseSearchResult(r.Id!, r.Title, ParseYear(r.Date), r.TrackCount, r.Score ?? 0))
            .ToList();
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

    private static MusicBrainzRecording MapRecording(RecordingDto r)
    {
        var artist = BuildArtistCredit(r.ArtistCredit);
        var release = r.Releases is { Count: > 0 } ? r.Releases[0] : null;
        var releaseGroup = release?.ReleaseGroup;

        var primaryType = string.IsNullOrWhiteSpace(releaseGroup?.PrimaryType)
            ? null
            : releaseGroup!.PrimaryType!.ToLowerInvariant();
        var secondaryTypes = releaseGroup?.SecondaryTypes?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.ToLowerInvariant())
            .ToList() ?? [];
        var releaseTypes = primaryType is null
            ? MultiValue.Join(secondaryTypes)
            : MultiValue.Join(new[] { primaryType }.Concat(secondaryTypes));

        var totalDiscs = release?.Media is { Count: > 0 } media ? media.Count : (int?)null;
        var totalTracks = release?.Media is { Count: > 0 } m
            ? m.Sum(x => x.TrackCount ?? 0) is var sum && sum > 0 ? sum : (int?)null
            : null;

        return new MusicBrainzRecording(
            Id: r.Id,
            Title: r.Title ?? string.Empty,
            Artist: artist,
            AlbumArtist: ArtistCreditNormalizer.GetPrimaryArtist(artist),
            ReleaseId: release?.Id,
            ReleaseTitle: release?.Title,
            Year: ParseYear(release?.Date),
            Isrc: r.Isrcs is { Count: > 0 } ? r.Isrcs[0] : null,
            LengthMs: r.Length,
            Score: r.Score ?? 100,
            CandidateCount: 1,
            Artists: BuildDiscreteArtists(r.ArtistCredit),
            ArtistMusicBrainzIds: BuildArtistIds(r.ArtistCredit),
            AlbumArtistMusicBrainzId: r.ArtistCredit is { Count: > 0 } ? r.ArtistCredit[0].Artist?.Id : null,
            ReleaseGroupId: releaseGroup?.Id,
            ReleaseTypePrimary: primaryType,
            ReleaseTypes: releaseTypes,
            IsCompilation: secondaryTypes.Contains("compilation"),
            TotalDiscs: totalDiscs,
            TotalTracks: totalTracks);
    }

    private static MusicBrainzRelease MapRelease(ReleaseDetailDto r)
    {
        var artist = BuildArtistCredit(r.ArtistCredit);
        var media = r.Media ?? [];

        var tracks = new List<MusicBrainzReleaseTrack>();
        foreach (var medium in media)
        {
            var disc = medium.Position ?? 1;
            if (medium.Tracks is null) continue;
            foreach (var t in medium.Tracks)
            {
                tracks.Add(new MusicBrainzReleaseTrack(
                    DiscNumber: disc,
                    // `number` is the printed track designation (can be non-numeric on vinyl, e.g. "A1");
                    // `position` is the reliable 1-based ordinal. Prefer position.
                    TrackNumber: t.Position ?? 0,
                    Title: t.Title ?? t.Recording?.Title,
                    LengthMs: t.Length ?? t.Recording?.Length,
                    RecordingId: t.Recording?.Id));
            }
        }

        var totalDiscs = media.Count > 0 ? media.Count : (int?)null;
        var totalTracks = media.Count > 0
            ? media.Sum(m => m.TrackCount ?? (m.Tracks?.Count ?? 0)) is var sum && sum > 0 ? sum : (int?)null
            : null;

        return new MusicBrainzRelease(
            Id: r.Id,
            Title: r.Title,
            AlbumArtist: string.IsNullOrWhiteSpace(artist) ? null : ArtistCreditNormalizer.GetPrimaryArtist(artist),
            Year: ParseYear(r.Date),
            TotalDiscs: totalDiscs,
            TotalTracks: totalTracks,
            Tracks: tracks);
    }

    private static string BuildArtistCredit(List<ArtistCreditDto>? credits)
    {
        if (credits is null or { Count: 0 })
            return string.Empty;

        return string.Concat(credits.Select(c => (c.Name ?? c.Artist?.Name ?? string.Empty) + (c.JoinPhrase ?? string.Empty))).Trim();
    }

    // Discrete artist names (one per credited artist, no join phrases) for the multi-value ARTISTS tag.
    private static string? BuildDiscreteArtists(List<ArtistCreditDto>? credits)
        => credits is null or { Count: 0 }
            ? null
            : MultiValue.Join(credits.Select(c => c.Artist?.Name ?? c.Name));

    // Per-artist MBIDs, positionally aligned with BuildDiscreteArtists (one entry per credited artist).
    private static string? BuildArtistIds(List<ArtistCreditDto>? credits)
        => credits is null or { Count: 0 }
            ? null
            : MultiValue.Join(credits.Select(c => c.Artist?.Id));

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            return null;
        return int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
    }

    private static string EscapeLucene(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    // --- JSON DTOs ---

    private sealed class IsrcDto
    {
        [JsonPropertyName("recordings")]
        public List<RecordingDto>? Recordings { get; set; }
    }

    private sealed class SearchDto
    {
        [JsonPropertyName("recordings")]
        public List<RecordingDto>? Recordings { get; set; }
    }

    private sealed class RecordingDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("length")]
        public int? Length { get; set; }

        [JsonPropertyName("score")]
        public int? Score { get; set; }

        [JsonPropertyName("artist-credit")]
        public List<ArtistCreditDto>? ArtistCredit { get; set; }

        [JsonPropertyName("releases")]
        public List<ReleaseDto>? Releases { get; set; }

        [JsonPropertyName("isrcs")]
        public List<string>? Isrcs { get; set; }
    }

    private sealed class ArtistCreditDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("joinphrase")]
        public string? JoinPhrase { get; set; }

        [JsonPropertyName("artist")]
        public ArtistDto? Artist { get; set; }
    }

    private sealed class ArtistDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("release-group")]
        public ReleaseGroupDto? ReleaseGroup { get; set; }

        [JsonPropertyName("media")]
        public List<MediaDto>? Media { get; set; }
    }

    private sealed class ReleaseGroupDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("primary-type")]
        public string? PrimaryType { get; set; }

        [JsonPropertyName("secondary-types")]
        public List<string?>? SecondaryTypes { get; set; }
    }

    private sealed class MediaDto
    {
        [JsonPropertyName("position")]
        public int? Position { get; set; }

        [JsonPropertyName("track-count")]
        public int? TrackCount { get; set; }

        [JsonPropertyName("tracks")]
        public List<TrackDto>? Tracks { get; set; }
    }

    // --- Release-detail (full tracklist) DTOs ---

    private sealed class ReleaseDetailDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("artist-credit")]
        public List<ArtistCreditDto>? ArtistCredit { get; set; }

        [JsonPropertyName("media")]
        public List<MediaDto>? Media { get; set; }
    }

    private sealed class ReleaseSearchDto
    {
        [JsonPropertyName("releases")]
        public List<ReleaseSearchItemDto>? Releases { get; set; }
    }

    private sealed class ReleaseSearchItemDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("track-count")]
        public int? TrackCount { get; set; }

        [JsonPropertyName("score")]
        public int? Score { get; set; }
    }

    private sealed class TrackDto
    {
        [JsonPropertyName("position")]
        public int? Position { get; set; }

        [JsonPropertyName("number")]
        public string? Number { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("length")]
        public int? Length { get; set; }

        [JsonPropertyName("recording")]
        public RecordingRefDto? Recording { get; set; }
    }

    private sealed class RecordingRefDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("length")]
        public int? Length { get; set; }
    }
}
