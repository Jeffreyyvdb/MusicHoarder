using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Navidrome;

/// <summary>
/// The status probe distinguishes three states a user needs told apart: turned off, not set up,
/// and set up but unreachable. Collapsing any two of them was the reason Navidrome was invisible.
/// </summary>
public class NavidromeStatusTests
{
    private static NavidromeOptions Configured(bool enabled = true) => new()
    {
        Enabled = enabled,
        BaseUrl = "https://navidrome.example",
        Username = "admin",
        Password = "pw",
    };

    [Fact]
    public async Task Disabled_ReportsNotConfiguredAndNeverPings()
    {
        var client = new StubClient(pingResult: true);

        var result = await NavidromeEndpoints.BuildStatusAsync(Configured(enabled: false), client, default);

        Assert.False(result.Enabled);
        Assert.False(result.Configured);
        Assert.False(result.Connected);
        Assert.Null(result.BaseUrl);
        // Pinging a deliberately-disabled integration would be pointless work every poll.
        Assert.Equal(0, client.PingCount);
    }

    [Fact]
    public async Task EnabledButMissingCredentials_ReportsEnabledAndUnconfigured()
    {
        var client = new StubClient(pingResult: true);
        var options = new NavidromeOptions { Enabled = true, BaseUrl = "https://navidrome.example" };

        var result = await NavidromeEndpoints.BuildStatusAsync(options, client, default);

        // The distinction that matters: it's switched on, it just isn't finished being set up.
        Assert.True(result.Enabled);
        Assert.False(result.Configured);
        Assert.False(result.Connected);
        Assert.Equal(0, client.PingCount);
    }

    [Fact]
    public async Task Configured_AndReachable_ReportsConnectedWithBaseUrl()
    {
        var client = new StubClient(pingResult: true);

        var result = await NavidromeEndpoints.BuildStatusAsync(Configured(), client, default);

        Assert.True(result.Enabled);
        Assert.True(result.Configured);
        Assert.True(result.Connected);
        Assert.Equal("https://navidrome.example", result.BaseUrl);
        Assert.Equal(1, client.PingCount);
    }

    [Fact]
    public async Task Configured_ButUnreachable_StaysConfiguredAndDisconnected()
    {
        var client = new StubClient(pingResult: false);

        var result = await NavidromeEndpoints.BuildStatusAsync(Configured(), client, default);

        // Configured stays true — a server that's down is not a server that's unconfigured, and
        // conflating them would send the user to edit settings that are already correct.
        Assert.True(result.Configured);
        Assert.False(result.Connected);
        Assert.Equal("https://navidrome.example", result.BaseUrl);
    }

    private sealed class StubClient(bool pingResult) : INavidromeClient
    {
        public int PingCount { get; private set; }

        public Task<bool> PingAsync(CancellationToken ct)
        {
            PingCount++;
            return Task.FromResult(pingResult);
        }

        public Task<IReadOnlyList<NavidromeSong>> GetStarredSongsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NavidromeSong>> SearchSongsAsync(string query, int limit, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task StarAsync(string songId, CancellationToken ct) => throw new NotSupportedException();

        public Task UnstarAsync(string songId, CancellationToken ct) => throw new NotSupportedException();
    }
}
