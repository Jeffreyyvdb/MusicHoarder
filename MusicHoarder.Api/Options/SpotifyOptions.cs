using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Spotify OAuth settings. Redirect URIs must be registered in the
/// <see href="https://developer.spotify.com/dashboard">Spotify Developer Dashboard</see> (Settings → Redirect URIs)
/// and must match exactly what the API sends as <c>redirect_uri</c> (authorize + token exchange).
/// </summary>
/// <remarks>
/// <para>
/// Spotify rejects <c>localhost</c> in redirect URIs and forbids wildcards, so a per-environment URI (each local
/// run and PR preview has a different origin) cannot be registered. Instead we register a single <b>relay</b> URI on
/// the production frontend — see <see cref="OAuthRelayUrl"/>. The relay is a stateless browser bounce that reads the
/// originating environment from the signed OAuth <c>state</c> and 303-redirects back to that environment's own
/// <c>/api/spotify/callback</c>, where the token exchange completes (with the owner's session cookie present).
/// </para>
/// <para>
/// <b>All environments</b> (local dev, PR previews, prod) set <see cref="OAuthRelayUrl"/> to the same value and use it
/// verbatim as <c>redirect_uri</c> in both the authorize request and the token exchange. Register exactly that one URI
/// — e.g. <c>https://your-prod-frontend/api/spotify/relay</c> — in the Spotify dashboard.
/// </para>
/// <para>
/// <see cref="OAuthRedirectBaseUrl"/> remains as a fallback for offline/standalone dev: when <see cref="OAuthRelayUrl"/>
/// is empty, the redirect URI is <c>OAuthRedirectBaseUrl + /api/spotify/callback</c>; when both are empty it is derived
/// from the current request (<c>scheme://host/api/spotify/callback</c>).
/// </para>
/// </remarks>
public class SpotifyOptions
{
    public const string SectionName = "Spotify";

    /// <summary>
    /// Spotify app Client ID. Prefer <c>dotnet user-secrets set "Spotify:ClientId" "…"</c> for local dev so
    /// credentials survive database resets when the <c>SpotifySettings</c> row is empty.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Spotify app Client Secret. Use user-secrets or environment variables; do not commit real values.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Absolute URL of the single registered OAuth relay (e.g. <c>https://your-prod-frontend/api/spotify/relay</c>),
    /// used verbatim as <c>redirect_uri</c>. Takes precedence over <see cref="OAuthRedirectBaseUrl"/>. Set identically
    /// in every environment so the value matches the one URI registered in the Spotify dashboard.
    /// </summary>
    public string OAuthRelayUrl { get; set; } = string.Empty;

    /// <summary>
    /// Fixed API base URL for OAuth <c>redirect_uri</c> (no trailing slash). Fallback used only when
    /// <see cref="OAuthRelayUrl"/> is empty; <c>/api/spotify/callback</c> is appended. When both are empty the
    /// redirect URI is derived from the request.
    /// </summary>
    public string OAuthRedirectBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 key that signs the OAuth <c>state</c> so the relay can verify it is one we issued (the state
    /// carries the originating environment's origin, which the relay bounces the browser back to — an unsigned value
    /// would be an open redirect of the auth code). Must be identical in the relay (frontend) and every API that
    /// completes an exchange. When empty, an opaque random state is used and no relay-origin validation occurs
    /// (offline/standalone dev only).
    /// </summary>
    public string OAuthStateSigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Maximum age (minutes) of a signed OAuth <c>state</c> accepted on the callback. Guards against replay of a stale
    /// authorization. The default of 10 minutes is the effective TTL in every environment (prod, preview, local) once
    /// the signing key is set. Ignored when <see cref="OAuthStateSigningKey"/> is empty.
    /// </summary>
    public int OAuthStateTtlMinutes { get; set; } = 10;

    /// <summary>
    /// How often (seconds) the background service checks whether the owner's Spotify access token is
    /// expiring and refreshes it. Must stay well below the 5-minute refresh buffer, so it's capped at
    /// 180s — a longer interval could let a token lapse between checks and break every Spotify-dependent
    /// stage. Default 60.
    /// </summary>
    [Range(15, 180)]
    public int SpotifyTokenRefreshIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How often to refresh Spotify liked-song ↔ local library match cache in the background (0 = disabled).
    /// </summary>
    public int LibraryMatchSyncIntervalMinutes { get; set; } = 120;

    /// <summary>
    /// How often the wishlist sync runs a <em>full</em> sweep — every auto-synced source (Liked Songs
    /// and playlists) paged to completion, so playlist edits and any likes the fast poll's shallow
    /// window missed are reconciled (0 = disabled, no sync at all).
    /// </summary>
    public int WishlistSyncIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// How often (seconds) to run the cheap near-real-time Liked-Songs poll between full sweeps: a
    /// single first-page fetch (Spotify returns newest-first) bounded by <see cref="WishlistFastPollMaxPages"/>,
    /// so a newly liked song reaches the wishlist in ~2 minutes instead of waiting up to the full
    /// <see cref="WishlistSyncIntervalMinutes"/>. Only Liked-Songs auto-synced sources are polled this
    /// way (playlists are reconciled on the full sweep). 0 disables the fast poll (full sweeps only).
    /// Requires <see cref="WishlistSyncIntervalMinutes"/> &gt; 0.
    /// </summary>
    public int LikedSongsFastPollSeconds { get; set; } = 120;

    /// <summary>
    /// Page cap for the fast Liked-Songs poll (50 tracks/page). The default of 2 covers a burst of new
    /// likes since the last poll while keeping the call cheap; anything older is caught by the next full
    /// sweep. Bounded so a page of all-already-present tracks can't make the fast poll page forever.
    /// </summary>
    public int WishlistFastPollMaxPages { get; set; } = 2;
}
