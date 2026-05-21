using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifyRedirectUriResolverTests
{
    [Fact]
    public void RelayUrl_wins_and_is_used_verbatim()
    {
        var options = new SpotifyOptions
        {
            OAuthRelayUrl = "https://prod.example/api/spotify/relay/",
            OAuthRedirectBaseUrl = "http://127.0.0.1:5142",
        };

        var uri = SpotifyRedirectUriResolver.Resolve(options, "https", "localhost:65284");

        Assert.Equal("https://prod.example/api/spotify/relay", uri);
    }

    [Fact]
    public void RedirectBaseUrl_used_when_no_relay()
    {
        var options = new SpotifyOptions { OAuthRedirectBaseUrl = "http://127.0.0.1:5142" };

        var uri = SpotifyRedirectUriResolver.Resolve(options, "https", "localhost:65284");

        Assert.Equal("http://127.0.0.1:5142/api/spotify/callback", uri);
    }

    [Fact]
    public void Falls_back_to_request_origin()
    {
        var uri = SpotifyRedirectUriResolver.Resolve(new SpotifyOptions(), "https", "localhost:65284");

        Assert.Equal("https://localhost:65284/api/spotify/callback", uri);
    }
}
