using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Endpoints;

public static class SpotifyEndpoints
{
    public static IEndpointRouteBuilder MapSpotifyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spotify").WithTags("Spotify");

        group.MapGet("/connect", async (
                HttpContext context,
                ISpotifyOAuthService spotifyOAuth,
                IOptions<SpotifyOptions> spotifyOptions,
                IOptions<FrontendOptions> frontendOptions,
                CancellationToken ct) =>
            {
                var redirectUri = ResolveOAuthRedirectUri(context.Request, spotifyOptions.Value);
                // The relay bounces the browser back here; encode this env's own public origin so it knows where.
                var returnOrigin = NormalizePublicBaseUrl(frontendOptions.Value.PublicBaseUrl);

                try
                {
                    var result = await spotifyOAuth.GetAuthorizationUrlAsync(redirectUri, returnOrigin, ct);
                    return Results.Ok(new { authorizationUrl = result.AuthorizationUrl, state = result.State });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { message = ex.Message });
                }
            })
            .WithName("SpotifyConnect")
            .WithSummary("Returns the Spotify authorization URL to initiate the OAuth flow.")
            .RequireOwner();

        group.MapGet("/callback", async (
                string? code,
                string? state,
                string? error,
                string? error_description,
                HttpContext context,
                ISpotifyOAuthService spotifyOAuth,
                IOptions<FrontendOptions> frontendOptions,
                IOptions<SpotifyOptions> spotifyOptions,
                CancellationToken ct) =>
            {
                var redirectUri = ResolveOAuthRedirectUri(context.Request, spotifyOptions.Value);
                var baseUrl = NormalizePublicBaseUrl(frontendOptions.Value.PublicBaseUrl);
                var useBrowserRedirect = baseUrl.Length > 0;

                // The frontend's server-side /api/spotify/callback route forwards the request here
                // (Node→API). A 302 back to that hop is unreadable to it (undici turns redirect:'manual'
                // into an opaque response with no Location), so it would lose the outcome. When that
                // route asks for JSON, return the outcome directly and let it own the browser redirect.
                var jsonMode = string.Equals(
                    context.Request.Headers["X-Spotify-Callback-Mode"],
                    "json",
                    StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrEmpty(error))
                {
                    var msg = error_description ?? error;
                    if (jsonMode)
                        return Results.Ok(new { connected = false, error = msg });
                    if (useBrowserRedirect)
                        return Results.Redirect($"{baseUrl}/spotify?spotify_error={Uri.EscapeDataString(msg)}");
                    return Results.BadRequest(new { message = msg });
                }

                if (string.IsNullOrEmpty(code))
                {
                    const string missing = "Authorization code missing.";
                    if (jsonMode)
                        return Results.Ok(new { connected = false, error = missing });
                    if (useBrowserRedirect)
                        return Results.Redirect($"{baseUrl}/spotify?spotify_error={Uri.EscapeDataString(missing)}");
                    return Results.BadRequest(new { message = missing });
                }

                // When the relay flow is active, the state is signed and carries this env's own origin. Reject any
                // state we didn't issue (forged/tampered/replayed, or minted for a different environment) before we
                // exchange the code — this is the CSRF/open-redirect guard for the relay bounce.
                var signingKey = spotifyOptions.Value.OAuthStateSigningKey;
                if (!string.IsNullOrEmpty(signingKey))
                {
                    var ttl = TimeSpan.FromMinutes(Math.Max(1, spotifyOptions.Value.OAuthStateTtlMinutes));
                    var stateOk = SpotifyOAuthStateProtector.TryValidate(state, signingKey, ttl, out var stateOrigin)
                        && (baseUrl.Length == 0 || string.Equals(stateOrigin, baseUrl, StringComparison.OrdinalIgnoreCase));
                    if (!stateOk)
                    {
                        const string badState = "Invalid or expired OAuth state.";
                        if (jsonMode)
                            return Results.Ok(new { connected = false, error = badState });
                        if (useBrowserRedirect)
                            return Results.Redirect($"{baseUrl}/spotify?spotify_error={Uri.EscapeDataString(badState)}");
                        return Results.BadRequest(new { message = badState });
                    }
                }

                var result = await spotifyOAuth.ExchangeCodeAsync(code, redirectUri, ct);
                if (!result.Success)
                {
                    var failMsg = result.Error ?? "Could not complete Spotify connection.";
                    if (jsonMode)
                        return Results.Ok(new { connected = false, error = failMsg });
                    if (useBrowserRedirect)
                        return Results.Redirect($"{baseUrl}/spotify?spotify_error={Uri.EscapeDataString(failMsg)}");
                    return Results.BadRequest(new { message = failMsg });
                }

                if (jsonMode)
                    return Results.Ok(new { connected = true, error = (string?)null });

                if (useBrowserRedirect)
                    return Results.Redirect($"{baseUrl}/spotify?spotify_connected=1");

                return Results.Ok(new { message = "Spotify account connected successfully." });
            })
            .WithName("SpotifyCallback")
            .WithSummary("Handles the OAuth callback from Spotify, exchanges the authorization code for tokens.");

        group.MapGet("/status", async (ISpotifyOAuthService spotifyOAuth, ICurrentUserAccessor currentUser, CancellationToken ct) =>
            {
                // Spotify state is the OWNER's (services resolve WellKnownUsers.OwnerId regardless of
                // caller), so report a clean "not connected" to non-owners rather than leaking it.
                if (currentUser.User?.IsOwner != true)
                    return Results.Ok(new { connected = false, connectedAt = (DateTime?)null, hasCredentials = false, tokenExpired = false });

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
            .WithSummary("Clears all stored Spotify tokens and disconnects the account.")
            .RequireOwner();

        group.MapPut("/credentials", async (SpotifyCredentialsRequest body, ISpotifyOAuthService spotifyOAuth, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.ClientId) || string.IsNullOrWhiteSpace(body.ClientSecret))
                    return Results.BadRequest(new { message = "ClientId and ClientSecret are required." });

                await spotifyOAuth.SaveCredentialsAsync(body.ClientId, body.ClientSecret, ct);
                return Results.Ok(new { message = "Spotify credentials saved." });
            })
            .WithName("SaveSpotifyCredentials")
            .WithSummary("Save Spotify API client credentials (ClientId and ClientSecret).")
            .RequireOwner();

        group.MapGet("/credentials", async (ISpotifyOAuthService spotifyOAuth, ICurrentUserAccessor currentUser, CancellationToken ct) =>
            {
                if (currentUser.User?.IsOwner != true)
                    return Results.Ok(new { clientId = (string?)null, hasClientSecret = false });

                var creds = await spotifyOAuth.GetCredentialsAsync(ct);
                return Results.Ok(new { clientId = creds.ClientId, hasClientSecret = creds.HasClientSecret });
            })
            .WithName("GetSpotifyCredentials")
            .WithSummary("Returns the configured Spotify client ID and whether a secret is set.");

        group.MapGet("/liked-songs", async (int? offset, int? limit, ISpotifyApiService spotifyApi, ISpotifyLibraryComparisonService comparisonService, CancellationToken ct) =>
            {
                try
                {
                    var result = await spotifyApi.GetLikedSongsAsync(offset ?? 0, limit ?? 50, ct);
                    var items = await comparisonService.AttachLibraryMatchesAsync(result.Items, ct);
                    return Results.Ok(result with { Items = items });
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
            .WithSummary("Returns paginated liked songs from the user's Spotify library.")
            .AddEndpointFilter<SpotifyOwnerReadFilter>();

        group.MapGet("/liked-songs/comparison", async (int? offset, int? limit, string? matchStatus, ISpotifyLibraryComparisonService comparisonService, CancellationToken ct) =>
            {
                try
                {
                    ComparisonMatchStatus? statusFilter = null;
                    if (!string.IsNullOrWhiteSpace(matchStatus))
                    {
                        if (!Enum.TryParse(matchStatus, ignoreCase: true, out ComparisonMatchStatus parsed))
                        {
                            return Results.BadRequest(new { error = "invalid_match_status", message = "matchStatus must be InLibrary, PossibleMatch, or NotInLibrary." });
                        }

                        statusFilter = parsed;
                    }

                    var result = await comparisonService.CompareAsync(offset ?? 0, limit ?? 50, statusFilter, ct);
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
            .WithName("GetSpotifyLikedSongsComparison")
            .WithSummary("Compares Spotify liked songs against the local library and returns match statuses.")
            .AddEndpointFilter<SpotifyOwnerReadFilter>();

        group.MapGet("/liked-songs/comparison/summary", async (ISpotifyLibraryComparisonService comparisonService, CancellationToken ct) =>
            {
                try
                {
                    var result = await comparisonService.GetSummaryAsync(ct);
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
            .WithName("GetSpotifyLikedSongsComparisonSummary")
            .WithSummary("Returns a summary of how many liked songs are in the library, possible matches, or missing.")
            .AddEndpointFilter<SpotifyOwnerReadFilter>();

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
            .WithSummary("Returns all user playlists (owned and followed) from Spotify.")
            .AddEndpointFilter<SpotifyOwnerReadFilter>();

        group.MapGet("/playlists/{playlistId}/tracks", async (string playlistId, int? offset, int? limit, ISpotifyApiService spotifyApi, ISpotifyLibraryComparisonService comparisonService, CancellationToken ct) =>
            {
                try
                {
                    var result = await spotifyApi.GetPlaylistTracksAsync(playlistId, offset ?? 0, limit ?? 50, ct);
                    var items = await comparisonService.AttachLibraryMatchesAsync(result.Items, ct);
                    return Results.Ok(result with { Items = items });
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
            .WithSummary("Returns paginated tracks for a specific Spotify playlist.")
            .AddEndpointFilter<SpotifyOwnerReadFilter>();

        return app;
    }

    private static string NormalizePublicBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        return url.Trim().TrimEnd('/');
    }

    private static string ResolveOAuthRedirectUri(HttpRequest request, SpotifyOptions spotifyOptions)
        => SpotifyRedirectUriResolver.Resolve(spotifyOptions, request.Scheme, request.Host.ToString());
}

public record SpotifyCredentialsRequest(string ClientId, string ClientSecret);

/// <summary>
/// Spotify read endpoints operate on the OWNER's connection (the services resolve
/// <c>WellKnownUsers.OwnerId</c> regardless of the caller), so a non-owner must never receive the
/// owner's Spotify data. Short-circuits to the same <c>spotify_not_connected</c> contract the
/// frontend already handles, so a demo user sees a clean "not connected" state instead of a leak.
/// </summary>
internal sealed class SpotifyOwnerReadFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserAccessor>();
        if (accessor.User?.IsOwner != true)
            return Results.Json(new { error = "spotify_not_connected" }, statusCode: StatusCodes.Status401Unauthorized);

        return await next(context);
    }
}
