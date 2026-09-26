using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Chat;

/// <summary>A link card's contents: what the link is called, by whom, and its artwork.</summary>
public sealed record LinkPreview(string? Title, string? Subtitle, string? ImageUrl);

public interface ILinkPreviewService
{
    /// <summary>
    /// Resolves a Spotify short link to the <c>open.spotify.com</c> link behind it; returns the
    /// input unchanged for anything else, or when it cannot be resolved.
    /// </summary>
    Task<ParsedChatLink> ResolveAsync(ParsedChatLink link, CancellationToken ct);

    /// <summary>A card for a Spotify or YouTube link; null for any other site or when nothing answered.</summary>
    Task<LinkPreview?> PreviewAsync(ParsedChatLink link, CancellationToken ct);
}

/// <summary>
/// Link cards for Spotify and YouTube. Spotify tracks come from the catalog API when the instance
/// has Spotify app credentials (title, artists and cover in one call), everything else from the
/// providers' public oEmbed endpoints.
///
/// <para>
/// Only fixed, known hosts are ever requested — the oEmbed endpoints and Spotify's short-link host —
/// with the user's URL as a query parameter. An arbitrary site is never fetched (that would let a
/// chat message make the server request an address of the sender's choosing), so a link to anywhere
/// else is shown as its bare host. A preview is best effort: it is bounded by
/// <see cref="Timeout"/>, and a message is sent without one rather than held back.
/// </para>
/// </summary>
public sealed partial class LinkPreviewService(
    IHttpClientFactory httpClients,
    ISpotifyCatalogSearchService spotifyCatalog,
    IServiceScopeFactory scopes,
    IOwnerLookupService ownerLookup,
    IOptions<SpotifyOptions> spotifyOptions,
    ILogger<LinkPreviewService> logger) : ILinkPreviewService
{
    public const string HttpClientName = "ChatLinkPreview";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<ParsedChatLink> ResolveAsync(ParsedChatLink link, CancellationToken ct)
    {
        if (link.Provider != "spotify" || link.Kind is not null) return link;
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
            || uri.Host is not ("spotify.link" or "spotify.app.link"))
            return link;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            // Redirects are not followed by this client: the hop is read, not taken.
            using var response = await httpClients.CreateClient(HttpClientName)
                .GetAsync(new Uri($"https://{uri.Host}{uri.PathAndQuery}"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location.ToString() : new Uri(uri, location).ToString();
                var resolved = ChatLinkParser.Classify(target);
                if (resolved.Provider == "spotify" && resolved.Kind is not null) return resolved;
            }

            // The short-link service may answer a page instead, naming its target in the markup.
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var html = await ReadCappedAsync(response, 128 * 1024, timeout.Token);
                var embedded = SpotifyUrlInPageRegex().Match(html);
                if (embedded.Success)
                {
                    var resolved = ChatLinkParser.Classify(embedded.Value);
                    if (resolved.Kind is not null) return resolved;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not resolve a Spotify short link");
        }
        return link;
    }

    public async Task<LinkPreview?> PreviewAsync(ParsedChatLink link, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            return link switch
            {
                { Provider: "spotify", Kind: "track", Id: { } id } =>
                    await SpotifyTrackAsync(id, timeout.Token) ?? await SpotifyOEmbedAsync(link, timeout.Token),
                { Provider: "spotify", Kind: not null } => await SpotifyOEmbedAsync(link, timeout.Token),
                { Provider: "youtube", Kind: not null } => await YouTubeOEmbedAsync(link, timeout.Token),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "No preview for a {Provider} link", link.Provider);
            return FallbackPreview(link);
        }
    }

    private async Task<LinkPreview?> SpotifyTrackAsync(string trackId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        // The instance's Spotify app belongs to the admin's settings row; a member's request has
        // none of its own, and the lookup is app-level (client credentials), not a user's account.
        var settings = await db.SpotifySettings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerLookup.OwnerUserId, ct);
        var (clientId, clientSecret) = SpotifyAppCredentialsResolver.Resolve(settings, spotifyOptions.Value);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)) return null;

        var track = await spotifyCatalog.GetTrackAsync(clientId, clientSecret, trackId, ct);
        if (track is null) return null;
        var artist = string.IsNullOrWhiteSpace(track.Artists) ? track.Artist : track.Artists;
        return new LinkPreview(track.Title, artist, track.AlbumArtUrl);
    }

    private async Task<LinkPreview?> SpotifyOEmbedAsync(ParsedChatLink link, CancellationToken ct)
    {
        var doc = await GetJsonAsync($"https://open.spotify.com/oembed?url={Uri.EscapeDataString(link.Url)}", ct);
        if (doc is null) return FallbackPreview(link);
        using (doc)
        {
            var root = doc.RootElement;
            return new LinkPreview(Text(root, "title"), null, HttpsOrNull(Text(root, "thumbnail_url")));
        }
    }

    private async Task<LinkPreview?> YouTubeOEmbedAsync(ParsedChatLink link, CancellationToken ct)
    {
        // oEmbed knows www.youtube.com URLs; a YouTube Music link names the same video.
        var target = link.Kind == "playlist"
            ? $"https://www.youtube.com/playlist?list={link.Id}"
            : $"https://www.youtube.com/watch?v={link.Id}";
        var doc = await GetJsonAsync($"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(target)}", ct);
        if (doc is null) return FallbackPreview(link);
        using (doc)
        {
            var root = doc.RootElement;
            return new LinkPreview(
                Text(root, "title"),
                Text(root, "author_name"),
                HttpsOrNull(Text(root, "thumbnail_url")) ?? FallbackPreview(link)?.ImageUrl);
        }
    }

    /// <summary>What a card can still say when the provider did not answer.</summary>
    private static LinkPreview? FallbackPreview(ParsedChatLink link) =>
        link is { Provider: "youtube", Kind: "video", Id: { } id }
            ? new LinkPreview(null, null, $"https://i.ytimg.com/vi/{id}/hqdefault.jpg")
            : null;

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var total = 0;
        int read;
        while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct)) > 0)
            total += read;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Trim(value.GetString(), 512)
            : null;

    private static string? HttpsOrNull(string? url) =>
        url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && url.Length <= 2048 ? url : null;

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    [GeneratedRegex(@"https://open\.spotify\.com/(?:intl-[a-z]{2}(?:-[a-z]{2})?/)?(?:track|album|playlist|artist|episode|show)/[A-Za-z0-9]{10,40}", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyUrlInPageRegex();
}
