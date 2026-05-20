using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifyOAuthServiceTests
{
    [Fact]
    public async Task GetAuthorizationUrl_WithCredentials_ReturnsValidUrl()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var result = await service.GetAuthorizationUrlAsync("http://localhost/callback");

        Assert.Contains("https://accounts.spotify.com/authorize", result.AuthorizationUrl);
        Assert.Contains("client_id=test-client-id", result.AuthorizationUrl);
        Assert.Contains("redirect_uri=http", result.AuthorizationUrl);
        Assert.Contains("user-library-read", result.AuthorizationUrl);
        Assert.Contains("playlist-read-private", result.AuthorizationUrl);
        Assert.Contains("playlist-read-collaborative", result.AuthorizationUrl);
        Assert.Contains("response_type=code", result.AuthorizationUrl);
        Assert.NotEmpty(result.State);
    }

    [Fact]
    public async Task GetAuthorizationUrl_WithoutCredentials_ThrowsInvalidOperationException()
    {
        await using var db = CreateDb();
        var service = CreateService(db, spotifyOptions: Microsoft.Extensions.Options.Options.Create(new SpotifyOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetAuthorizationUrlAsync("http://localhost/callback"));
    }

    [Fact]
    public async Task GetAuthorizationUrl_WithCredentialsFromConfig_WhenDbHasNoSecrets_ReturnsValidUrl()
    {
        await using var db = CreateDb();
        var spotifyOpts = Microsoft.Extensions.Options.Options.Create(new SpotifyOptions
        {
            ClientId = "from-config-id",
            ClientSecret = "from-config-secret",
        });
        var service = CreateService(db, spotifyOptions: spotifyOpts);

        var result = await service.GetAuthorizationUrlAsync("http://localhost/callback");

        Assert.Contains("client_id=from-config-id", result.AuthorizationUrl);
    }

    [Fact]
    public async Task GetStatus_NoSettings_ReturnsDisconnected()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var status = await service.GetStatusAsync();

        Assert.False(status.Connected);
        Assert.Null(status.ConnectedAt);
        Assert.False(status.HasCredentials);
        Assert.False(status.TokenExpired);
    }

    [Fact]
    public async Task GetStatus_Connected_ReturnsCorrectState()
    {
        await using var db = CreateDb();
        var connectedAt = DateTime.UtcNow.AddHours(-1);
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            TokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            ConnectedAtUtc = connectedAt,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var status = await service.GetStatusAsync();

        Assert.True(status.Connected);
        Assert.Equal(connectedAt, status.ConnectedAt);
        Assert.True(status.HasCredentials);
        Assert.False(status.TokenExpired);
    }

    [Fact]
    public async Task GetStatus_ExpiredToken_ReturnsTokenExpired()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
            ConnectedAtUtc = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var status = await service.GetStatusAsync();

        Assert.True(status.Connected);
        Assert.True(status.TokenExpired);
    }

    [Fact]
    public async Task Disconnect_ClearsTokens()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            TokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            ConnectedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.DisconnectAsync();

        var settings = await db.SpotifySettings.FirstAsync();
        Assert.Null(settings.AccessToken);
        Assert.Null(settings.RefreshToken);
        Assert.Null(settings.TokenExpiresAtUtc);
        Assert.Null(settings.ConnectedAtUtc);
        Assert.Equal("client-id", settings.ClientId);
        Assert.Equal("client-secret", settings.ClientSecret);
    }

    [Fact]
    public async Task SaveCredentials_StoresValues()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await service.SaveCredentialsAsync("my-client-id", "my-client-secret");

        var settings = await db.SpotifySettings.FirstAsync();
        Assert.Equal("my-client-id", settings.ClientId);
        Assert.Equal("my-client-secret", settings.ClientSecret);
    }

    [Fact]
    public async Task SaveCredentials_UpdatesExisting()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "old-id",
            ClientSecret = "old-secret",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.SaveCredentialsAsync("new-id", "new-secret");

        var settings = await db.SpotifySettings.FirstAsync();
        Assert.Equal("new-id", settings.ClientId);
        Assert.Equal("new-secret", settings.ClientSecret);
    }

    [Fact]
    public async Task GetCredentials_ReturnsCorrectValues()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "my-client-id",
            ClientSecret = "my-secret",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetCredentialsAsync();

        Assert.Equal("my-client-id", result.ClientId);
        Assert.True(result.HasClientSecret);
    }

    [Fact]
    public async Task GetCredentials_NoSettings_ReturnsEmpty()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.GetCredentialsAsync();

        Assert.Null(result.ClientId);
        Assert.False(result.HasClientSecret);
    }

    [Fact]
    public async Task ExchangeCode_WithoutCredentials_ReturnsFailed()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.ExchangeCodeAsync("code", "http://localhost/callback");

        Assert.False(result.Success);
        Assert.Contains("credentials not configured", result.Error);
    }

    [Fact]
    public async Task ExchangeCode_SuccessfulResponse_StoresTokens()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
        });
        await db.SaveChangesAsync();

        var tokenResponse = new
        {
            access_token = "new-access-token",
            token_type = "Bearer",
            scope = "user-library-read",
            expires_in = 3600,
            refresh_token = "new-refresh-token",
        };

        var handler = new FakeHttpHandler(HttpStatusCode.OK, JsonSerializer.Serialize(tokenResponse));
        var service = CreateService(db, handler);

        var result = await service.ExchangeCodeAsync("auth-code", "http://localhost/callback");

        Assert.True(result.Success);

        var settings = await db.SpotifySettings.FirstAsync();
        Assert.Equal("new-access-token", settings.AccessToken);
        Assert.Equal("new-refresh-token", settings.RefreshToken);
        Assert.NotNull(settings.TokenExpiresAtUtc);
        Assert.NotNull(settings.ConnectedAtUtc);
    }

    [Fact]
    public async Task ExchangeCode_FailedResponse_ReturnsError()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
        });
        await db.SaveChangesAsync();

        var handler = new FakeHttpHandler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
        var service = CreateService(db, handler);

        var result = await service.ExchangeCodeAsync("bad-code", "http://localhost/callback");

        Assert.False(result.Success);
        Assert.Contains("BadRequest", result.Error);
    }

    [Fact]
    public async Task RefreshAccessToken_WithoutRefreshToken_ReturnsFailed()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var result = await service.RefreshAccessTokenAsync();

        Assert.False(result.Success);
        Assert.Contains("No refresh token", result.Error);
    }

    [Fact]
    public async Task RefreshAccessToken_SuccessfulResponse_UpdatesToken()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AccessToken = "old-access",
            RefreshToken = "existing-refresh",
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
            ConnectedAtUtc = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var tokenResponse = new
        {
            access_token = "refreshed-access-token",
            token_type = "Bearer",
            scope = "user-library-read",
            expires_in = 3600,
        };

        var handler = new FakeHttpHandler(HttpStatusCode.OK, JsonSerializer.Serialize(tokenResponse));
        var service = CreateService(db, handler);

        var result = await service.RefreshAccessTokenAsync();

        Assert.True(result.Success);

        var settings = await db.SpotifySettings.FirstAsync();
        Assert.Equal("refreshed-access-token", settings.AccessToken);
        Assert.Equal("existing-refresh", settings.RefreshToken);
        Assert.True(settings.TokenExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task RefreshAccessToken_WithNewRefreshToken_UpdatesBothTokens()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
            ConnectedAtUtc = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var tokenResponse = new
        {
            access_token = "refreshed-access",
            token_type = "Bearer",
            scope = "user-library-read",
            expires_in = 3600,
            refresh_token = "rotated-refresh",
        };

        var handler = new FakeHttpHandler(HttpStatusCode.OK, JsonSerializer.Serialize(tokenResponse));
        var service = CreateService(db, handler);

        var result = await service.RefreshAccessTokenAsync();

        Assert.True(result.Success);

        var settings = await db.SpotifySettings.FirstAsync();
        Assert.Equal("refreshed-access", settings.AccessToken);
        Assert.Equal("rotated-refresh", settings.RefreshToken);
    }

    [Fact]
    public async Task EnsureValidToken_NotConnected_DoesNotRefresh()
    {
        await using var db = CreateDb();
        var callCount = 0;
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}", () => callCount++);
        var service = CreateService(db, handler);

        await service.EnsureValidTokenAsync();

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task EnsureValidToken_TokenNotExpiringSoon_DoesNotRefresh()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AccessToken = "access",
            RefreshToken = "refresh",
            TokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            ConnectedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var callCount = 0;
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}", () => callCount++);
        var service = CreateService(db, handler);

        await service.EnsureValidTokenAsync();

        Assert.Equal(0, callCount);
    }

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static SpotifyOAuthService CreateService(
        MusicHoarderDbContext db,
        HttpMessageHandler? handler = null,
        IOptions<SpotifyOptions>? spotifyOptions = null)
    {
        var scopeFactory = new SpotifyScopeFactory(db);
        var httpClient = new HttpClient(handler ?? new FakeHttpHandler(HttpStatusCode.OK, "{}"));
        var logger = NullLogger<SpotifyOAuthService>.Instance;
        spotifyOptions ??= Microsoft.Extensions.Options.Options.Create(new SpotifyOptions());
        return new SpotifyOAuthService(scopeFactory, httpClient, new TestOwnerLookupService(), spotifyOptions, logger);
    }

    private sealed class FakeHttpHandler(
        HttpStatusCode statusCode,
        string responseBody,
        Action? onSend = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend?.Invoke();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SpotifyScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SimpleScope(new SimpleServiceProvider(db));
    }

    private sealed class SimpleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class SimpleServiceProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            return null;
        }
    }
}
