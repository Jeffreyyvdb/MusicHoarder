using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifySettingsTests
{
    [Fact]
    public void IsConnected_WithTokens_ReturnsTrue()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
        };

        Assert.True(settings.IsConnected);
    }

    [Fact]
    public void IsConnected_WithoutAccessToken_ReturnsFalse()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            RefreshToken = "refresh-token",
        };

        Assert.False(settings.IsConnected);
    }

    [Fact]
    public void IsConnected_WithoutRefreshToken_ReturnsFalse()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            AccessToken = "access-token",
        };

        Assert.False(settings.IsConnected);
    }

    [Fact]
    public void HasCredentials_WithBothSet_ReturnsTrue()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
            ClientSecret = "client-secret",
        };

        Assert.True(settings.HasCredentials);
    }

    [Fact]
    public void HasCredentials_MissingClientId_ReturnsFalse()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientSecret = "client-secret",
        };

        Assert.False(settings.HasCredentials);
    }

    [Fact]
    public void HasCredentials_MissingClientSecret_ReturnsFalse()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "client-id",
        };

        Assert.False(settings.HasCredentials);
    }

    [Fact]
    public void IsTokenExpiringSoon_WithinBuffer_ReturnsTrue()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(3),
        };

        Assert.True(settings.IsTokenExpiringSoon(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void IsTokenExpiringSoon_OutsideBuffer_ReturnsFalse()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        };

        Assert.False(settings.IsTokenExpiringSoon(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void IsTokenExpiringSoon_NullExpiry_ReturnsFalse()
    {
        var settings = new SpotifySettings();

        Assert.False(settings.IsTokenExpiringSoon(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void StoreTokens_SetsAllFields()
    {
        var settings = new SpotifySettings();

        settings.StoreTokens("access", "refresh", 3600);

        Assert.Equal("access", settings.AccessToken);
        Assert.Equal("refresh", settings.RefreshToken);
        Assert.NotNull(settings.TokenExpiresAtUtc);
        Assert.NotNull(settings.ConnectedAtUtc);
        Assert.True(settings.TokenExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public void StoreTokens_DoesNotOverwriteExistingConnectedAt()
    {
        var originalConnectedAt = DateTime.UtcNow.AddHours(-1);
        var settings = new SpotifySettings {     OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ConnectedAtUtc = originalConnectedAt };

        settings.StoreTokens("access", "refresh", 3600);

        Assert.Equal(originalConnectedAt, settings.ConnectedAtUtc);
    }

    [Fact]
    public void UpdateAccessToken_UpdatesTokenAndExpiry()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            AccessToken = "old",
            RefreshToken = "old-refresh",
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
        };

        settings.UpdateAccessToken("new-access", 3600);

        Assert.Equal("new-access", settings.AccessToken);
        Assert.Equal("old-refresh", settings.RefreshToken);
        Assert.True(settings.TokenExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public void UpdateAccessToken_WithNewRefreshToken_UpdatesRefreshToken()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            AccessToken = "old",
            RefreshToken = "old-refresh",
        };

        settings.UpdateAccessToken("new-access", 3600, "new-refresh");

        Assert.Equal("new-access", settings.AccessToken);
        Assert.Equal("new-refresh", settings.RefreshToken);
    }

    [Fact]
    public void ClearTokens_ClearsAllTokenFields()
    {
        var settings = new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            AccessToken = "access",
            RefreshToken = "refresh",
            TokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            ConnectedAtUtc = DateTime.UtcNow,
        };

        settings.ClearTokens();

        Assert.Null(settings.AccessToken);
        Assert.Null(settings.RefreshToken);
        Assert.Null(settings.TokenExpiresAtUtc);
        Assert.Null(settings.ConnectedAtUtc);
        Assert.False(settings.IsConnected);
    }
}
