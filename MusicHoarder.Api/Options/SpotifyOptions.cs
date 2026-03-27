namespace MusicHoarder.Api.Options;

/// <summary>
/// Spotify OAuth settings. Redirect URIs must be registered in the
/// <see href="https://developer.spotify.com/dashboard">Spotify Developer Dashboard</see> (Settings → Redirect URIs)
/// and must match exactly what the API sends as <c>redirect_uri</c> (authorize + token exchange).
/// </summary>
/// <remarks>
/// <para>
/// Spotify rejects <c>localhost</c> in redirect URIs; use a loopback IP such as <c>127.0.0.1</c> (see Spotify&apos;s
/// migration guide). For example register <c>http://127.0.0.1:5142/api/spotify/callback</c> and set
/// <see cref="OAuthRedirectBaseUrl"/> to <c>http://127.0.0.1:5142</c>.
/// </para>
/// <para>
/// <b>Dynamic API port (e.g. Aspire):</b> Use one fixed dev URL in the dashboard and matching
/// <see cref="OAuthRedirectBaseUrl"/>. After Spotify redirects, if nothing is listening on that port, edit the
/// address bar to your real API origin (same path <c>/api/spotify/callback</c>, keep <c>code</c> and <c>state</c>).
/// The callback still uses <see cref="OAuthRedirectBaseUrl"/> for the token request so it matches Spotify.
/// </para>
/// <para>
/// When <see cref="OAuthRedirectBaseUrl"/> is empty, <c>redirect_uri</c> is built from the current HTTP request
/// (<c>scheme://host/api/spotify/callback</c>). Use that when the listening port is stable and registered in Spotify.
/// </para>
/// </remarks>
public class SpotifyOptions
{
    public const string SectionName = "Spotify";

    /// <summary>
    /// Fixed API base URL for OAuth <c>redirect_uri</c> (no trailing slash), e.g. <c>http://127.0.0.1:5142</c>.
    /// Must match a redirect URI registered in Spotify. When empty, the redirect URI is derived from the request.
    /// </summary>
    public string OAuthRedirectBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// How often to refresh Spotify liked-song ↔ local library match cache in the background (0 = disabled).
    /// </summary>
    public int LibraryMatchSyncIntervalMinutes { get; set; } = 120;
}
