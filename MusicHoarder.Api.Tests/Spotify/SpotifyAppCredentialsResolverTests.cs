using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifyAppCredentialsResolverTests
{
    [Fact]
    public void Resolve_DbHasBoth_UsesDb()
    {
        var db = new SpotifySettings { ClientId = "db-id", ClientSecret = "db-secret" };
        var opts = new SpotifyOptions { ClientId = "cfg-id", ClientSecret = "cfg-secret" };

        var (id, secret) = SpotifyAppCredentialsResolver.Resolve(db, opts);

        Assert.Equal("db-id", id);
        Assert.Equal("db-secret", secret);
    }

    [Fact]
    public void Resolve_DbIncomplete_UsesConfig()
    {
        var db = new SpotifySettings { ClientId = "db-id", ClientSecret = null };
        var opts = new SpotifyOptions { ClientId = "cfg-id", ClientSecret = "cfg-secret" };

        var (id, secret) = SpotifyAppCredentialsResolver.Resolve(db, opts);

        Assert.Equal("cfg-id", id);
        Assert.Equal("cfg-secret", secret);
    }

    [Fact]
    public void Resolve_NullDb_UsesConfig()
    {
        var opts = new SpotifyOptions { ClientId = "x", ClientSecret = "y" };

        var (id, secret) = SpotifyAppCredentialsResolver.Resolve(null, opts);

        Assert.Equal("x", id);
        Assert.Equal("y", secret);
    }

    [Fact]
    public void Resolve_NoDbNoConfig_ReturnsNull()
    {
        var (id, secret) = SpotifyAppCredentialsResolver.Resolve(null, new SpotifyOptions());
        Assert.Null(id);
        Assert.Null(secret);
    }
}
