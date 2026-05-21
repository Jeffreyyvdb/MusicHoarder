using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifyOAuthStateProtectorTests
{
    private const string Key = "test-signing-key-0123456789";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    [Fact]
    public void Create_then_validate_round_trips_return_origin()
    {
        var state = SpotifyOAuthStateProtector.Create("https://localhost:65284", Key);

        var ok = SpotifyOAuthStateProtector.TryValidate(state, Key, Ttl, out var origin);

        Assert.True(ok);
        Assert.Equal("https://localhost:65284", origin);
    }

    [Fact]
    public void Two_states_for_same_origin_differ_via_nonce()
    {
        var a = SpotifyOAuthStateProtector.Create("https://localhost:65284", Key);
        var b = SpotifyOAuthStateProtector.Create("https://localhost:65284", Key);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Validate_fails_with_wrong_key()
    {
        var state = SpotifyOAuthStateProtector.Create("https://localhost:65284", Key);

        Assert.False(SpotifyOAuthStateProtector.TryValidate(state, "a-different-key", Ttl, out _));
    }

    [Fact]
    public void Validate_fails_when_payload_tampered()
    {
        var state = SpotifyOAuthStateProtector.Create("https://localhost:65284", Key);
        var dot = state.IndexOf('.');
        // Flip a character in the payload segment, keep the original signature.
        var payload = state[..dot];
        var mutatedChar = payload[0] == 'A' ? 'B' : 'A';
        var tampered = mutatedChar + payload[1..] + state[dot..];

        Assert.False(SpotifyOAuthStateProtector.TryValidate(tampered, Key, Ttl, out _));
    }

    [Fact]
    public void Validate_fails_when_expired()
    {
        var state = SpotifyOAuthStateProtector.Create("https://localhost:65284", Key);

        // A zero TTL makes any positive age exceed it.
        Assert.False(SpotifyOAuthStateProtector.TryValidate(state, Key, TimeSpan.Zero, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot-here")]
    [InlineData("only.")]
    [InlineData(".onlysig")]
    [InlineData("not!base64.also!bad")]
    public void Validate_fails_on_malformed_input(string? state)
    {
        Assert.False(SpotifyOAuthStateProtector.TryValidate(state, Key, Ttl, out _));
    }

    [Fact]
    public void Validate_fails_when_no_signing_key()
    {
        var state = SpotifyOAuthStateProtector.Create("https://localhost:65284", Key);

        Assert.False(SpotifyOAuthStateProtector.TryValidate(state, "", Ttl, out _));
    }

    [Fact]
    public void Create_throws_without_key()
    {
        Assert.Throws<ArgumentException>(() => SpotifyOAuthStateProtector.Create("https://x", ""));
    }
}
