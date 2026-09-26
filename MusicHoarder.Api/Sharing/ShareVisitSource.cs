namespace MusicHoarder.Api.Sharing;

/// <summary>
/// Reads the little a share-link visit says about itself: whether it came from a machine rather
/// than a person, and which app or site it was tapped in.
///
/// <para>
/// Both answers are best-effort. The share page reports opens with a script beacon, so the usual
/// link-preview crawlers — which fetch the HTML for its og-tags and run nothing — never reach the
/// counter in the first place; <see cref="IsLikelyBot"/> is the belt to that pair of braces. The
/// source comes from the page's <c>document.referrer</c> first (a site, or Chrome's
/// <c>android-app://package</c> form for a link opened from an Android app), then from the in-app
/// browser markers apps put in their user agents, because an in-app browser — TikTok's, Instagram's
/// — usually sends no referrer at all.
/// </para>
/// </summary>
public static class ShareVisitSource
{
    private const int MaxLength = 64;

    // Substrings of user agents that are not a person opening a link. Case-insensitive. WhatsApp's
    // own user agent only ever fetches link previews: the app opens links in the system browser.
    private static readonly string[] BotMarkers =
    [
        "bot", "crawl", "spider", "slurp", "bytespider", "facebookexternalhit", "facebookcatalog", "whatsapp/",
        "embedly", "iframely", "preview", "headless", "lighthouse", "pagespeed", "curl/", "wget/",
        "python-", "go-http-client", "java/", "httpclient", "axios/", "node-fetch", "scrapy",
    ];

    // Registrable domains (a host matches the domain itself or any subdomain of it).
    private static readonly (string Domain, string Source)[] Sites =
    [
        ("tiktok.com", "TikTok"), ("tiktokv.com", "TikTok"),
        ("instagram.com", "Instagram"),
        ("facebook.com", "Facebook"), ("fb.com", "Facebook"), ("messenger.com", "Messenger"),
        ("threads.net", "Threads"), ("threads.com", "Threads"),
        ("t.co", "X"), ("twitter.com", "X"), ("x.com", "X"),
        ("youtube.com", "YouTube"), ("youtu.be", "YouTube"),
        ("reddit.com", "Reddit"),
        ("discord.com", "Discord"), ("discordapp.com", "Discord"),
        ("whatsapp.com", "WhatsApp"),
        ("t.me", "Telegram"), ("telegram.org", "Telegram"),
        ("snapchat.com", "Snapchat"),
        ("linkedin.com", "LinkedIn"), ("lnkd.in", "LinkedIn"),
        ("bsky.app", "Bluesky"),
        ("soundcloud.com", "SoundCloud"),
    ];

    // Android package names, as Chrome reports them for a link opened from another app.
    private static readonly (string Package, string Source)[] AndroidApps =
    [
        ("com.zhiliaoapp.musically", "TikTok"), ("com.ss.android.ugc.trill", "TikTok"),
        ("com.instagram.android", "Instagram"), ("com.instagram.barcelona", "Threads"),
        ("com.facebook.katana", "Facebook"), ("com.facebook.orca", "Messenger"),
        ("com.whatsapp", "WhatsApp"), ("com.whatsapp.w4b", "WhatsApp"),
        ("org.telegram.messenger", "Telegram"),
        ("com.discord", "Discord"),
        ("com.twitter.android", "X"),
        ("com.snapchat.android", "Snapchat"),
        ("com.reddit.frontpage", "Reddit"),
        ("com.google.android.youtube", "YouTube"),
        ("com.google.android.gm", "Gmail"),
        ("com.linkedin.android", "LinkedIn"),
        ("com.google.android.apps.messaging", "Messages"),
    ];

    // In-app browser markers, checked when the referrer said nothing. Bots are filtered before this
    // runs, so "Twitter" here cannot be Twitterbot.
    private static readonly (string Marker, string Source)[] InAppMarkers =
    [
        ("musical_ly", "TikTok"), ("BytedanceWebview", "TikTok"), ("TikTok", "TikTok"), ("trill_", "TikTok"),
        ("Instagram", "Instagram"),
        ("Barcelona", "Threads"),
        ("FBAN/", "Facebook"), ("FBAV/", "Facebook"), ("FB_IAB", "Facebook"),
        ("Snapchat", "Snapchat"),
        ("Twitter", "X"),
        ("LinkedInApp", "LinkedIn"),
        ("Discord", "Discord"),
        ("Reddit", "Reddit"),
        ("Telegram", "Telegram"),
        (" Line/", "LINE"),
    ];

    /// <summary>True for a missing user agent or one that names a crawler, preview fetcher or script.</summary>
    public static bool IsLikelyBot(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return true;
        foreach (var marker in BotMarkers)
        {
            if (userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The app or site a visit came from: a known name, else the referring host (or Android
    /// package), else null.
    /// </summary>
    public static string? Classify(string? referrer, string? userAgent) =>
        FromReferrer(referrer) ?? FromUserAgent(userAgent);

    private static string? FromReferrer(string? referrer)
    {
        if (string.IsNullOrWhiteSpace(referrer) || !Uri.TryCreate(referrer.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme.Equals("android-app", StringComparison.OrdinalIgnoreCase))
        {
            var package = uri.Host.ToLowerInvariant();
            if (package.Length == 0)
                return null;
            foreach (var (known, source) in AndroidApps)
            {
                if (package == known)
                    return source;
            }
            return Clip(package);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0)
            return null;
        foreach (var (domain, source) in Sites)
        {
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                return source;
        }
        // google.com, google.nl, www.google.co.uk, …
        if (host.StartsWith("google.", StringComparison.Ordinal) || host.Contains(".google.", StringComparison.Ordinal))
            return "Google";

        return Clip(host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host);
    }

    private static string? FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;
        foreach (var (marker, source) in InAppMarkers)
        {
            if (userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return source;
        }
        return null;
    }

    private static string Clip(string value) => value.Length <= MaxLength ? value : value[..MaxLength];
}
