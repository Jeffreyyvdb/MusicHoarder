namespace MusicHoarder.Api.Download;

/// <summary>
/// Shared handling of yt-dlp's stderr: maps the signatures that actually bite a server deployment to
/// an actionable owner-facing hint, and keeps the salient tail of the output for diagnostics.
/// Used by both the wishlist downloader and the URL-import probe so a failure reads the same either way.
/// </summary>
public static class YtDlpErrors
{
    /// <summary>
    /// True when yt-dlp resolved the video but offered nothing downloadable. This is an environment
    /// problem (bot check / missing PO token / client set), never a property of the track: the same
    /// video downloads fine from a client YouTube still serves plain HTTPS formats to.
    /// </summary>
    public static bool LooksLikeNoUsableFormats(string? stderr) =>
        !string.IsNullOrWhiteSpace(stderr)
        && (stderr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Only images are available", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps common yt-dlp stderr signatures to an actionable owner-facing hint. The ones that bite a
    /// headless server are the datacenter bot check, a missing JS runtime, and the PO-token/format
    /// dead end. Returns null when nothing matches (the caller falls back to raw stderr).
    /// </summary>
    public static string? Classify(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;

        bool Has(string s) => stderr.Contains(s, StringComparison.OrdinalIgnoreCase);

        // Checked before the bot check: YouTube rejects the player-API request outright ("Precondition
        // check failed" behind repeated HTTP 400s) when yt-dlp asks with client parameters it has
        // retired. The extraction then falls through to the bot-check message, so that error is the
        // symptom and the stale binary is the cause worth naming.
        if (Has("Precondition check failed"))
            return "YouTube rejected this server's yt-dlp player requests (\"Precondition check failed\"), which usually means the yt-dlp binary in the container is out of date. Redeploy so the image rebuilds it, or update it in place.";
        if (Has("Sign in to confirm") || Has("not a bot") || Has("confirm you’re") || Has("confirm you're"))
            return "YouTube is asking this server to sign in (datacenter bot check). Set MusicEnricher:YtDlpCookiesPath to a cookies.txt exported from a logged-in browser.";
        if (Has("No supported JavaScript runtime"))
            return "yt-dlp needs a JavaScript runtime (deno) to read YouTube, and none was found on the server.";
        if (Has("Private video"))
            return "That video is private.";
        if (Has("age") && Has("restrict"))
            return "That video is age-restricted and needs authenticated cookies.";
        if (Has("Video unavailable") || Has("This video is not available"))
            return "That video is unavailable.";
        if (Has("HTTP Error 429") || Has("Too Many Requests"))
            return "YouTube is rate-limiting this server. Try again shortly.";
        if (Has("Read-only file system") && Has("cookies"))
            return "yt-dlp couldn't write back the cookies file (read-only mount). The server needs a writable cookies copy.";
        // Checked after the cookie/bot hints above: those name the actual cause when both appear.
        if (LooksLikeNoUsableFormats(stderr))
            return Has("PO Token") || Has("po_token")
                ? "YouTube served no downloadable audio stream to this server: the clients it allowed need a PO token. Nothing is wrong with the track — retry later, or pick a different player client via MusicEnricher:YtDlpExtraArgs (e.g. --extractor-args youtube:player_client=default,-web,-web_safari)."
                : "YouTube served no downloadable audio stream to this server (bot check / player-client restriction). Nothing is wrong with the track — retry later, or pick a different player client via MusicEnricher:YtDlpExtraArgs.";
        return null;
    }

    /// <summary>
    /// The single error string surfaced to the owner: the classified hint when one matches, plus the
    /// raw tail for diagnostics. Falls back to the bare "exited N: …" form when nothing classifies.
    /// </summary>
    public static string Describe(int exitCode, string stderr)
    {
        var raw = $"exited {exitCode}: {Tail(stderr)}";
        var hint = Classify(stderr);
        return hint is null ? raw : $"{hint} ({raw})";
    }

    /// <summary>Last ~800 chars of stderr — the salient error (yt-dlp "ERROR:" / traceback exception)
    /// is at the end, so we keep the tail rather than the head.</summary>
    public static string Tail(string s, int max = 800)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
