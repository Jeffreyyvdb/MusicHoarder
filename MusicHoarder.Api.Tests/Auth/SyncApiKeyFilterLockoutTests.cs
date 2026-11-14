using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Auth;

public class SyncApiKeyFilterLockoutTests
{
    private const string Key = "dummy-sync-key-0123456789-abcdefghijklmnopqrstuvwxyz";

    private sealed class Monitor(SyncOptions o) : IOptionsMonitor<SyncOptions>
    {
        public SyncOptions CurrentValue => o;
        public SyncOptions Get(string? name) => o;
        public IDisposable? OnChange(Action<SyncOptions, string?> listener) => null;
    }

    private static async Task<int> CallAsync(SyncApiKeyFilter filter, string ip, string key)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        http.Request.Headers[SyncApiKeyFilter.HeaderName] = key;
        var ctx = new DefaultEndpointFilterInvocationContext(http);
        var result = await filter.InvokeAsync(ctx, _ => ValueTask.FromResult<object?>(Results.Ok()));
        return ((IStatusCodeHttpResult)result!).StatusCode ?? 200;
    }

    [Fact]
    public async Task Correct_key_is_accepted_from_an_address_that_has_used_up_its_failures()
    {
        var opts = new SyncOptions { Mode = SyncMode.Receive, ApiKey = Key, SyncedSourceDirectory = "/dummy/synced" };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new SyncApiKeyFilter(new Monitor(opts), cache, NullLogger<SyncApiKeyFilter>.Instance);

        for (var i = 0; i < 12; i++)
            await CallAsync(filter, "10.0.0.2", "wrong-" + i);

        Assert.Equal(200, await CallAsync(filter, "10.0.0.2", Key));
        // wrong keys from that address remain throttled
        Assert.Equal(429, await CallAsync(filter, "10.0.0.2", "wrong-again"));
    }
}
