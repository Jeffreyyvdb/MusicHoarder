using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

public class SpotifyOAuthService(
    IServiceScopeFactory scopeFactory,
    HttpClient httpClient,
    ILogger<SpotifyOAuthService> logger) : ISpotifyOAuthService
{
    private static readonly string[] Scopes =
    [
        "user-library-read",
        "playlist-read-private",
        "playlist-read-collaborative"
    ];

    private const string AuthorizeUrl = "https://accounts.spotify.com/authorize";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    public async Task<SpotifyConnectResult> GetAuthorizationUrlAsync(string redirectUri, CancellationToken ct = default)
    {
        var settings = await ReadSettingsAsync(ct);

        if (!settings.HasCredentials)
            throw new InvalidOperationException("Spotify ClientId and ClientSecret must be configured before connecting.");

        var state = GenerateState();
        var scopeString = string.Join(" ", Scopes);

        var url = $"{AuthorizeUrl}" +
                  $"?client_id={Uri.EscapeDataString(settings.ClientId!)}" +
                  $"&response_type=code" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&scope={Uri.EscapeDataString(scopeString)}" +
                  $"&state={Uri.EscapeDataString(state)}" +
                  $"&show_dialog=true";

        return new SpotifyConnectResult(url, state);
    }

    public async Task<SpotifyTokenResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var settings = await ReadSettingsAsync(ct);

        if (!settings.HasCredentials)
            return new SpotifyTokenResult(false, "Spotify credentials not configured.");

        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = requestBody,
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}")))
            }
        };

        try
        {
            var response = await httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Spotify token exchange failed: {StatusCode} {Body}", response.StatusCode, json);
                return new SpotifyTokenResult(false, $"Token exchange failed: {response.StatusCode}");
            }

            var tokenResponse = JsonSerializer.Deserialize<SpotifyTokenResponse>(json);
            if (tokenResponse is null)
                return new SpotifyTokenResult(false, "Failed to parse token response.");

            await MutateSettingsAsync(s =>
                s.StoreTokens(tokenResponse.AccessToken, tokenResponse.RefreshToken!, tokenResponse.ExpiresIn), ct);

            logger.LogInformation("Spotify OAuth tokens stored successfully");

            RunInitialLibraryMatchSyncAfterConnect();

            return new SpotifyTokenResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error exchanging Spotify authorization code");
            return new SpotifyTokenResult(false, "An error occurred during token exchange.");
        }
    }

    public async Task<SpotifyTokenResult> RefreshAccessTokenAsync(CancellationToken ct = default)
    {
        var settings = await ReadSettingsAsync(ct);

        if (string.IsNullOrWhiteSpace(settings.RefreshToken))
            return new SpotifyTokenResult(false, "No refresh token available.");

        if (!settings.HasCredentials)
            return new SpotifyTokenResult(false, "Spotify credentials not configured.");

        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = settings.RefreshToken,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = requestBody,
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}")))
            }
        };

        try
        {
            var response = await httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Spotify token refresh failed: {StatusCode} {Body}", response.StatusCode, json);
                return new SpotifyTokenResult(false, $"Token refresh failed: {response.StatusCode}");
            }

            var tokenResponse = JsonSerializer.Deserialize<SpotifyTokenResponse>(json);
            if (tokenResponse is null)
                return new SpotifyTokenResult(false, "Failed to parse refresh token response.");

            await MutateSettingsAsync(s =>
                s.UpdateAccessToken(tokenResponse.AccessToken, tokenResponse.ExpiresIn, tokenResponse.RefreshToken), ct);

            logger.LogInformation("Spotify access token refreshed successfully");
            return new SpotifyTokenResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error refreshing Spotify access token");
            return new SpotifyTokenResult(false, "An error occurred during token refresh.");
        }
    }

    public async Task<SpotifyStatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        var settings = await ReadSettingsAsync(ct);
        var tokenExpired = settings.IsConnected && settings.TokenExpiresAtUtc.HasValue
            && settings.TokenExpiresAtUtc.Value <= DateTime.UtcNow;

        return new SpotifyStatusResult(
            settings.IsConnected,
            settings.ConnectedAtUtc,
            settings.HasCredentials,
            tokenExpired);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await MutateSettingsAsync(s => s.ClearTokens(), ct);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var rows = await db.SpotifyTrackLibraryMatches.ToListAsync(ct);
        if (rows.Count > 0)
        {
            db.SpotifyTrackLibraryMatches.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Spotify account disconnected");
    }

    public async Task SaveCredentialsAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        await MutateSettingsAsync(s =>
        {
            s.ClientId = clientId;
            s.ClientSecret = clientSecret;
        }, ct);

        logger.LogInformation("Spotify credentials saved");
    }

    public async Task<SpotifyCredentialsResult> GetCredentialsAsync(CancellationToken ct = default)
    {
        var settings = await ReadSettingsAsync(ct);
        return new SpotifyCredentialsResult(settings.ClientId, !string.IsNullOrWhiteSpace(settings.ClientSecret));
    }

    public async Task EnsureValidTokenAsync(CancellationToken ct = default)
    {
        var settings = await ReadSettingsAsync(ct);

        if (!settings.IsConnected)
            return;

        if (settings.IsTokenExpiringSoon(RefreshBuffer))
        {
            logger.LogInformation("Spotify token expiring soon, refreshing...");
            await RefreshAccessTokenAsync(ct);
        }
    }

    private async Task<SpotifySettings> ReadSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var settings = await db.SpotifySettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not null) return settings;

        settings = new SpotifySettings();
        db.SpotifySettings.Add(settings);
        await db.SaveChangesAsync(ct);

        return settings;
    }

    private void RunInitialLibraryMatchSyncAfterConnect()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                using var scope = scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<ISpotifyLibraryComparisonService>();
                await sync.SyncLikedSongsMatchesAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Initial Spotify liked-song library match sync after connect failed (will retry on background interval)");
            }
        });
    }

    private async Task MutateSettingsAsync(Action<SpotifySettings> mutate, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var settings = await db.SpotifySettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new SpotifySettings();
            db.SpotifySettings.Add(settings);
        }

        mutate(settings);
        await db.SaveChangesAsync(ct);
    }

    private static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
