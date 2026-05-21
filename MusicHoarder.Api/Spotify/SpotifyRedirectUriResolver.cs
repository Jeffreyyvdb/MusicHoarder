using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Resolves the Spotify OAuth <c>redirect_uri</c>. Precedence: the registered relay URL (used verbatim) →
/// <see cref="SpotifyOptions.OAuthRedirectBaseUrl"/> + <c>/api/spotify/callback</c> → the request origin.
/// Pure so it can be unit-tested without an <c>HttpRequest</c>.
/// </summary>
public static class SpotifyRedirectUriResolver
{
    public static string Resolve(SpotifyOptions options, string requestScheme, string requestHost)
    {
        var relayUrl = options.OAuthRelayUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(relayUrl))
            return relayUrl;

        var baseUrl = options.OAuthRedirectBaseUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(baseUrl))
            return $"{baseUrl}/api/spotify/callback";

        return $"{requestScheme}://{requestHost}/api/spotify/callback";
    }
}
