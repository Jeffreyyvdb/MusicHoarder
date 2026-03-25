namespace MusicHoarder.Api.Spotify;

public interface ISpotifyOAuthService
{
    Task<SpotifyConnectResult> GetAuthorizationUrlAsync(string redirectUri, CancellationToken ct = default);
    Task<SpotifyTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);
    Task<SpotifyTokenResult> RefreshAccessTokenAsync(CancellationToken ct = default);
    Task<SpotifyStatusResult> GetStatusAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task SaveCredentialsAsync(string clientId, string clientSecret, CancellationToken ct = default);
    Task<SpotifyCredentialsResult> GetCredentialsAsync(CancellationToken ct = default);
    Task EnsureValidTokenAsync(CancellationToken ct = default);
}

public record SpotifyConnectResult(string AuthorizationUrl, string State);

public record SpotifyTokenResult(bool Success, string? Error = null);

public record SpotifyStatusResult(
    bool Connected,
    DateTime? ConnectedAt,
    bool HasCredentials,
    bool TokenExpired);

public record SpotifyCredentialsResult(string? ClientId, bool HasClientSecret);
