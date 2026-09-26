using System.Text.RegularExpressions;
using MusicHoarder.Api.Import;

namespace MusicHoarder.Api.Chat;

/// <summary>What a link in a chat message points at, as far as the chat cares.</summary>
/// <param name="Url">The URL to store and open: canonical for Spotify and YouTube (tracking
/// parameters such as Spotify's <c>si=</c>, which identifies the person who shared it, dropped),
/// as given otherwise.</param>
/// <param name="Provider"><c>spotify</c>, <c>youtube</c>, <c>musichoarder</c> (a share link of this
/// instance), or null.</param>
/// <param name="Kind"><c>track</c>, <c>album</c>, <c>playlist</c>, <c>artist</c>, <c>episode</c>,
/// <c>show</c>, <c>video</c>, <c>share</c> — or null when not known (a <c>spotify.link</c> short link).</param>
/// <param name="Id">The provider's id: a Spotify id, a YouTube video or playlist id, a share token.</param>
public sealed record ParsedChatLink(string Url, string? Provider, string? Kind, string? Id);

/// <summary>
/// Finds and classifies the link in a message. Pure, so both the send path and the tests use it.
/// What an app puts in a share sheet is rarely just the URL — Spotify on Android sends "…on
/// Spotify: https://open.spotify.com/track/…?si=…", YouTube a <c>youtu.be</c> link — so the first
/// http(s) URL anywhere in the text is the one that counts.
/// </summary>
public static partial class ChatLinkParser
{
    public const int MaxUrlLength = 2048;

    /// <summary>The first http(s) URL in <paramref name="text"/>, trailing punctuation trimmed.</summary>
    public static string? FindFirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = UrlRegex().Match(text);
        if (!match.Success) return null;

        var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '’', '”');
        // A closing bracket belongs to the URL only when it opened one inside it (Wikipedia-style).
        while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '('))
            url = url[..^1];
        return IsHttpUrl(url) ? url : null;
    }

    public static bool IsHttpUrl(string? value) =>
        value is { Length: > 0 and <= MaxUrlLength }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(uri.Host);

    /// <param name="ownHost">The host this instance's share links live on (the frontend's public
    /// host), so a pasted <c>/share/{token}</c> link can become a playable card.</param>
    public static ParsedChatLink Classify(string url, string? ownHost = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new ParsedChatLink(url, null, null, null);
        var host = uri.Host.ToLowerInvariant();

        if (!string.IsNullOrEmpty(ownHost) && string.Equals(host, ownHost, StringComparison.OrdinalIgnoreCase))
        {
            var share = OwnShareRegex().Match(uri.AbsolutePath);
            if (share.Success)
                return new ParsedChatLink(url, "musichoarder", "share", Uri.UnescapeDataString(share.Groups[1].Value));
        }

        if (host is "open.spotify.com" or "play.spotify.com")
        {
            var sp = SpotifyPathRegex().Match(uri.AbsolutePath);
            if (sp.Success)
            {
                var kind = sp.Groups[1].Value.ToLowerInvariant();
                var id = sp.Groups[2].Value;
                return new ParsedChatLink($"https://open.spotify.com/{kind}/{id}", "spotify", kind, id);
            }
            return new ParsedChatLink(url, "spotify", null, null);
        }
        if (host is "spotify.link" or "spotify.app.link")
            return new ParsedChatLink(url, "spotify", null, null);

        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtu.be" or "www.youtu.be")
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            var isMusic = host == "music.youtube.com";
            if (uri.AbsolutePath.TrimEnd('/').Equals("/playlist", StringComparison.OrdinalIgnoreCase)
                && query.TryGetValue("list", out var list) && PlaylistIdRegex().IsMatch(list.ToString()))
            {
                var listId = list.ToString();
                var canonical = isMusic
                    ? $"https://music.youtube.com/playlist?list={listId}"
                    : $"https://www.youtube.com/playlist?list={listId}";
                return new ParsedChatLink(canonical, "youtube", "playlist", listId);
            }
            if (ImportUrlParser.TryParse(url, out var importKind, out var videoId) && importKind == ImportUrlKind.YouTube)
            {
                var canonical = isMusic
                    ? $"https://music.youtube.com/watch?v={videoId}"
                    : ImportUrlParser.YouTubeWatchUrl(videoId);
                return new ParsedChatLink(canonical, "youtube", "video", videoId);
            }
            return new ParsedChatLink(url, "youtube", null, null);
        }

        return new ParsedChatLink(url, null, null, null);
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^/(?:intl-[a-z]{2}(?:-[a-z]{2})?/)?(track|album|playlist|artist|episode|show)/([A-Za-z0-9]{10,40})/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyPathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,64}$")]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@"^/share/([A-Za-z0-9_-]{8,64})/?$")]
    private static partial Regex OwnShareRegex();
}
