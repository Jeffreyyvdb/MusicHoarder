using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.RateLimiting;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>A reference may match any of <paramref name="DurationsSeconds"/>: a download can be a longer or
/// shorter cut than the track that was asked for.</summary>
internal sealed record ReferenceQuery(string Artist, string Title, string? Isrc, IReadOnlyList<double> DurationsSeconds)
{
    public bool Fits(double seconds) => DurationsSeconds.Any(d => Math.Abs(seconds - d) <= EditionReferenceClient.DurationToleranceSeconds);
}

/// <summary>A free 30 s preview of one edition of the song.</summary>
internal sealed record EditionReference(Edition Edition, string Id, string PreviewUrl);

/// <summary><see cref="Incomplete"/> is set when a catalog could not be asked, so an empty answer is not proof that the song has no references.</summary>
internal sealed record ReferenceLookup(IReadOnlyList<EditionReference> References, bool Incomplete);

/// <summary>Where the explicit and clean editions of a song come from.</summary>
internal interface IEditionReferenceClient
{
    Task<ReferenceLookup> FindAsync(ReferenceQuery query, CancellationToken ct);

    /// <summary>The preview's bytes, or null when it could not be fetched.</summary>
    Task<byte[]?> DownloadPreviewAsync(string url, CancellationToken ct);
}

/// <summary>
/// iTunes Search and Deezer as reference catalogs. Both flag every edition ("explicit" / "cleaned",
/// "explicit_content_lyrics" 1 / 3) and give a free preview. A result counts only when its
/// normalized title equals the song's and its length is within <see cref="DurationToleranceSeconds"/> of a
/// length the song is known by,
/// so a live cut or a remix is never a reference. Paced politely and never throws: a failure marks the
/// lookup incomplete.
/// </summary>
internal sealed class EditionReferenceClient(
    HttpClient http,
    IOptions<MusicEnricherOptions> options,
    ILogger<EditionReferenceClient> logger) : IEditionReferenceClient
{
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicHoarder-EditionCheck");
        return client;
    }
    public const double DurationToleranceSeconds = 4;

    internal static readonly TimeSpan DeezerInterval = TimeSpan.FromMilliseconds(350);

    /// <summary>Deezer's quota is 50 calls per 5 seconds; after a quota error, wait out one window.</summary>
    private static readonly TimeSpan DeezerQuotaBackoff = TimeSpan.FromSeconds(5);

    /// <summary>The whole preview download, body included: a stalled body must not hold the run.</summary>
    internal static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(30);

    private const int MaxPreviewBytes = 4 * 1024 * 1024;

    /// <summary>Previews kept per catalog and edition: compilations re-list one recording many times.</summary>
    internal const int MaxPreviewsPerEdition = 3;
    private const int DeezerQuotaErrorCode = 4;

    private readonly RequestRateGate _deezerGate = new();
    private long _deezerBackoffUntilUtcTicks;

    public async Task<ReferenceLookup> FindAsync(ReferenceQuery query, CancellationToken ct)
    {
        var found = new List<EditionReference>();
        var incomplete = false;
        var wanted = ReferenceTitle.Normalize(query.Title);
        if (wanted.Length == 0)
            return new ReferenceLookup([], false);

        try
        {
            found.AddRange(await SearchITunesAsync(query, wanted, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            incomplete = true;
            logger.LogDebug(ex, "iTunes reference lookup failed");
        }

        try
        {
            var (deezer, complete) = await SearchDeezerAsync(query, wanted, ct);
            found.AddRange(deezer);
            incomplete |= !complete;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            incomplete = true;
            logger.LogDebug(ex, "Deezer reference lookup failed");
        }

        return new ReferenceLookup(found, incomplete);
    }

    public async Task<byte[]?> DownloadPreviewAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PreviewTimeout);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPreviewBytes)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxPreviewBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Preview download failed");
            return null;
        }
    }

    private async Task<List<EditionReference>> SearchITunesAsync(ReferenceQuery query, string wanted, CancellationToken ct)
    {
        var term = Uri.EscapeDataString($"{ReferenceTitle.LeadArtist(query.Artist)} {ReferenceTitle.WithoutBrackets(query.Title)}");
        using var json = await GetITunesJsonAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=50&country=US", ct);

        var found = new List<EditionReference>();
        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return found;
        foreach (var track in results.EnumerateArray())
        {
            var edition = Text(track, "trackExplicitness") switch
            {
                "explicit" => (Edition?)Edition.Explicit,
                "cleaned" => Edition.Clean,
                _ => null,
            };
            var preview = Text(track, "previewUrl");
            if (edition is null || string.IsNullOrEmpty(preview)
                || ReferenceTitle.Normalize(Text(track, "trackName") ?? "") != wanted
                || !ReferenceTitle.SameArtist(query.Artist, Text(track, "artistName"))
                || !query.Fits(Number(track, "trackTimeMillis") / 1000))
                continue;
            found.Add(new EditionReference(edition.Value, $"itunes:{Number(track, "trackId").ToString("0", CultureInfo.InvariantCulture)}", preview));
        }
        return Capped(found);
    }

    private async Task<(List<EditionReference> References, bool Complete)> SearchDeezerAsync(
        ReferenceQuery query, string wanted, CancellationToken ct)
    {
        if (Interlocked.Read(ref _deezerBackoffUntilUtcTicks) > DateTime.UtcNow.Ticks)
            return ([], false);

        var tracks = new List<JsonElement>();
        var documents = new List<JsonDocument>();
        var complete = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(query.Isrc))
            {
                var byIsrc = await GetJsonAsync(
                    $"https://api.deezer.com/track/isrc:{Uri.EscapeDataString(query.Isrc)}", _deezerGate, DeezerInterval, ct);
                documents.Add(byIsrc);
                complete &= !IsQuotaError(byIsrc.RootElement);
                if (byIsrc.RootElement.TryGetProperty("id", out _))
                    tracks.Add(byIsrc.RootElement);
            }

            var q = Uri.EscapeDataString(
                $"artist:\"{Unquoted(ReferenceTitle.LeadArtist(query.Artist))}\" track:\"{Unquoted(ReferenceTitle.WithoutBrackets(query.Title)).Trim()}\"");
            var search = await GetJsonAsync($"https://api.deezer.com/search?q={q}", _deezerGate, DeezerInterval, ct);
            documents.Add(search);
            complete &= !IsQuotaError(search.RootElement);
            if (search.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                tracks.AddRange(data.EnumerateArray());

            var found = new List<EditionReference>();
            var seen = new HashSet<long>();
            foreach (var track in tracks)
            {
                var id = (long)Number(track, "id");
                var preview = Text(track, "preview");
                var edition = (int)Number(track, "explicit_content_lyrics") switch
                {
                    1 => (Edition?)Edition.Explicit,
                    3 => Edition.Clean,
                    _ => null,
                };
                if (!seen.Add(id) || string.IsNullOrEmpty(preview) || edition is null
                    || !query.Fits(Number(track, "duration"))
                    || ReferenceTitle.Normalize(Text(track, "title") ?? "") != wanted
                    || !ReferenceTitle.SameArtist(query.Artist, ArtistName(track)))
                    continue;
                found.Add(new EditionReference(edition.Value, $"deezer:{id}", preview));
            }
            if (!complete)
                Interlocked.Exchange(ref _deezerBackoffUntilUtcTicks, (DateTime.UtcNow + DeezerQuotaBackoff).Ticks);
            return (Capped(found), complete);
        }
        finally
        {
            foreach (var document in documents)
                document.Dispose();
        }
    }

    /// <summary>An iTunes call on the pace and backoff window every iTunes caller shares.</summary>
    private async Task<JsonDocument> GetITunesJsonAsync(string url, CancellationToken ct)
    {
        var wait = ITunesThrottle.BackoffUntil - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            throw new ProviderRateLimitedException(wait);

        using var lease = await ITunesThrottle.AcquireAsync(options.Value, ct);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("the iTunes pace granted no permit");

        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
        {
            var retryAfter = RetryAfterHeader.Read(response, DateTimeOffset.UtcNow) ?? ITunesThrottle.DefaultBackoff;
            ITunesThrottle.ExtendBackoff(retryAfter);
            throw new ProviderRateLimitedException(retryAfter);
        }
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, RequestRateGate gate, TimeSpan interval, CancellationToken ct)
    {
        await gate.WaitAsync(interval, ct);
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static bool IsQuotaError(JsonElement root) =>
        root.TryGetProperty("error", out var error)
        && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code)
        && code.TryGetInt32(out var value)
        && value == DeezerQuotaErrorCode;

    private static List<EditionReference> Capped(List<EditionReference> found) =>
        [.. found.GroupBy(r => r.Edition).SelectMany(g => g.Take(MaxPreviewsPerEdition))];

    private static string Unquoted(string value) => value.Replace("\"", "", StringComparison.Ordinal);

    private static string? ArtistName(JsonElement track) =>
        track.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object ? Text(artist, "name") : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}
