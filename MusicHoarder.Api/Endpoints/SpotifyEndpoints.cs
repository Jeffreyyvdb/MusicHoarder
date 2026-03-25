using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Endpoints;

public static class SpotifyEndpoints
{
    public static IEndpointRouteBuilder MapSpotifyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spotify").WithTags("Spotify");

        group.MapGet("/connect", async (HttpContext context, ISpotifyOAuthService spotifyOAuth, CancellationToken ct) =>
            {
                var request = context.Request;
                var redirectUri = $"{request.Scheme}://{request.Host}/api/spotify/callback";

                try
                {
                    var result = await spotifyOAuth.GetAuthorizationUrlAsync(redirectUri, ct);
                    return Results.Ok(new { authorizationUrl = result.AuthorizationUrl, state = result.State });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .WithName("SpotifyConnect")
            .WithSummary("Returns the Spotify authorization URL to initiate the OAuth flow.");

        group.MapGet("/callback", async (
                string? code,
                string? state,
                string? error,
                string? error_description,
                HttpContext context,
                ISpotifyOAuthService spotifyOAuth,
                IOptions<FrontendOptions> frontendOptions,
                CancellationToken ct) =>
            {
                var request = context.Request;
                var redirectUri = $"{request.Scheme}://{request.Host}/api/spotify/callback";
                var baseUrl = NormalizePublicBaseUrl(frontendOptions.Value.PublicBaseUrl);
                var useBrowserRedirect = baseUrl.Length > 0;

                if (!string.IsNullOrEmpty(error))
                {
                    var msg = error_description ?? error;
                    if (useBrowserRedirect)
                        return Results.Redirect($"{baseUrl}/spotify?spotify_error={Uri.EscapeDataString(msg)}");
                    return Results.BadRequest(new { message = msg });
                }

                if (string.IsNullOrEmpty(code))
                {
                    const string missing = "Authorization code missing.";
                    if (useBrowserRedirect)
                        return Results.Redirect($"{baseUrl}/spotify?spotify_error={Uri.EscapeDataString(missing)}");
                    return Results.BadRequest(new { message = missing });
                }

                var result = await spotifyOAuth.ExchangeCodeAsync(code, redirectUri, ct);
                if (!result.Success)
                {
                    const string genericFail = "Could not complete Spotify connection.";
                    if (useBrowserRedirect)
                        return Results.Redirect($"{baseUrl}/spotify?spotify_error={Uri.EscapeDataString(genericFail)}");
                    return Results.BadRequest(new { message = result.Error });
                }

                if (useBrowserRedirect)
                    return Results.Redirect($"{baseUrl}/spotify?spotify_connected=1");

                return Results.Ok(new { message = "Spotify account connected successfully." });
            })
            .WithName("SpotifyCallback")
            .WithSummary("Handles the OAuth callback from Spotify, exchanges the authorization code for tokens.");

        group.MapGet("/status", async (ISpotifyOAuthService spotifyOAuth, CancellationToken ct) =>
            {
                var status = await spotifyOAuth.GetStatusAsync(ct);
                return Results.Ok(new
                {
                    connected = status.Connected,
                    connectedAt = status.ConnectedAt,
                    hasCredentials = status.HasCredentials,
                    tokenExpired = status.TokenExpired,
                });
            })
            .WithName("SpotifyStatus")
            .WithSummary("Returns the current Spotify connection status.");

        group.MapDelete("/disconnect", async (ISpotifyOAuthService spotifyOAuth, CancellationToken ct) =>
            {
                await spotifyOAuth.DisconnectAsync(ct);
                return Results.Ok(new { message = "Spotify account disconnected." });
            })
            .WithName("SpotifyDisconnect")
            .WithSummary("Clears all stored Spotify tokens and disconnects the account.");

        group.MapPut("/credentials", async (SpotifyCredentialsRequest body, ISpotifyOAuthService spotifyOAuth, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.ClientId) || string.IsNullOrWhiteSpace(body.ClientSecret))
                    return Results.BadRequest(new { message = "ClientId and ClientSecret are required." });

                await spotifyOAuth.SaveCredentialsAsync(body.ClientId, body.ClientSecret, ct);
                return Results.Ok(new { message = "Spotify credentials saved." });
            })
            .WithName("SaveSpotifyCredentials")
            .WithSummary("Save Spotify API client credentials (ClientId and ClientSecret).");

        group.MapGet("/credentials", async (ISpotifyOAuthService spotifyOAuth, CancellationToken ct) =>
            {
                var creds = await spotifyOAuth.GetCredentialsAsync(ct);
                return Results.Ok(new { clientId = creds.ClientId, hasClientSecret = creds.HasClientSecret });
            })
            .WithName("GetSpotifyCredentials")
            .WithSummary("Returns the configured Spotify client ID and whether a secret is set.");

        group.MapGet("/liked-songs", async (int? offset, int? limit, ISpotifyApiService spotifyApi, CancellationToken ct) =>
            {
                try
                {
                    var result = await spotifyApi.GetLikedSongsAsync(offset ?? 0, limit ?? 50, ct);
                    return Results.Ok(result);
                }
                catch (SpotifyNotConnectedException)
                {
                    return Results.Json(new { error = "spotify_not_connected" }, statusCode: 401);
                }
                catch (SpotifyRateLimitException)
                {
                    return Results.Json(new { error = "rate_limit_exceeded" }, statusCode: 429);
                }
            })
            .WithName("GetSpotifyLikedSongs")
            .WithSummary("Returns paginated liked songs from the user's Spotify library.");

        group.MapGet("/playlists", async (ISpotifyApiService spotifyApi, CancellationToken ct) =>
            {
                try
                {
                    var result = await spotifyApi.GetPlaylistsAsync(ct);
                    return Results.Ok(result);
                }
                catch (SpotifyNotConnectedException)
                {
                    return Results.Json(new { error = "spotify_not_connected" }, statusCode: 401);
                }
                catch (SpotifyRateLimitException)
                {
                    return Results.Json(new { error = "rate_limit_exceeded" }, statusCode: 429);
                }
            })
            .WithName("GetSpotifyPlaylists")
            .WithSummary("Returns all user playlists (owned and followed) from Spotify.");

        group.MapGet("/playlists/{playlistId}/tracks", async (string playlistId, int? offset, int? limit, ISpotifyApiService spotifyApi, CancellationToken ct) =>
            {
                try
                {
                    var result = await spotifyApi.GetPlaylistTracksAsync(playlistId, offset ?? 0, limit ?? 50, ct);
                    return Results.Ok(result);
                }
                catch (SpotifyNotConnectedException)
                {
                    return Results.Json(new { error = "spotify_not_connected" }, statusCode: 401);
                }
                catch (SpotifyRateLimitException)
                {
                    return Results.Json(new { error = "rate_limit_exceeded" }, statusCode: 429);
                }
            })
            .WithName("GetSpotifyPlaylistTracks")
            .WithSummary("Returns paginated tracks for a specific Spotify playlist.");

        return app;
    }

    private static string NormalizePublicBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        return url.Trim().TrimEnd('/');
    }
}

public record SpotifyCredentialsRequest(string ClientId, string ClientSecret);
