using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Navidrome;

public class NavidromeSubsonicClientTests
{
    private const string Password = "s3cret-pw";

    [Fact]
    public async Task Star_BuildsSaltedTokenAuthUrl()
    {
        var handler = new CapturingHandler("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        var client = Client(handler);

        await client.StarAsync("song-123", default);

        var uri = handler.LastRequest!;
        Assert.Equal("/rest/star.view", uri.AbsolutePath);
        var q = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("admin", q["u"]);
        Assert.Equal("musichoarder", q["c"]);
        Assert.Equal("json", q["f"]);
        Assert.Equal("song-123", q["id"]);

        // The token is md5(password + salt) — never the raw password.
        var salt = q["s"]!;
        var expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Password + salt)));
        Assert.Equal(expected, q["t"]);
        Assert.DoesNotContain(Password, uri.Query);
    }

    [Fact]
    public async Task GetStarredSongs_ParsesSongsAndBlankMbidToNull()
    {
        const string json = """
        {"subsonic-response":{"status":"ok","starred2":{"song":[
          {"id":"a","title":"So Fast","artist":"Juice WRLD","album":"Leaks","path":"Juice WRLD/Leaks/So Fast.mp3","musicBrainzId":"","duration":156,"suffix":"mp3"},
          {"id":"b","title":"RoboCop","artist":"Kanye West","path":"Kanye West/808s/07 - RoboCop.flac","musicBrainzId":"202b37a8","duration":274,"suffix":"flac"}
        ]}}}
        """;
        var client = Client(new CapturingHandler(json));

        var songs = await client.GetStarredSongsAsync(default);

        Assert.Equal(2, songs.Count);
        Assert.Equal("a", songs[0].Id);
        Assert.Null(songs[0].MusicBrainzId); // "" normalized to null
        Assert.Equal(156, songs[0].DurationSeconds);
        Assert.Equal("202b37a8", songs[1].MusicBrainzId);
        Assert.Equal("Kanye West/808s/07 - RoboCop.flac", songs[1].Path);
    }

    [Fact]
    public async Task GetStarredSongs_EmptyWhenNoStarredElement()
    {
        var client = Client(new CapturingHandler("""{"subsonic-response":{"status":"ok"}}"""));
        Assert.Empty(await client.GetStarredSongsAsync(default));
    }

    [Fact]
    public async Task FailedEnvelope_ThrowsNavidromeApiException()
    {
        var json = """{"subsonic-response":{"status":"failed","error":{"code":40,"message":"Wrong username or password"}}}""";
        var client = Client(new CapturingHandler(json));

        var ex = await Assert.ThrowsAsync<NavidromeApiException>(() => client.PingAsyncThrowing(default));
        Assert.Equal(40, ex.Code);
    }

    [Fact]
    public async Task Ping_ReturnsFalseOnFailure_WithoutThrowing()
    {
        var client = Client(new CapturingHandler("""{"subsonic-response":{"status":"failed","error":{"code":40,"message":"nope"}}}"""));
        Assert.False(await client.PingAsync(default));
    }

    private static INavidromeClient Client(CapturingHandler handler)
    {
        var opts = new NavidromeOptions
        {
            Enabled = true,
            BaseUrl = "http://navidrome:4533",
            Username = "admin",
            Password = Password,
        };
        return new NavidromeSubsonicClient(
            new HttpClient(handler),
            new StaticMonitor<NavidromeOptions>(opts),
            NullLogger<NavidromeSubsonicClient>.Instance);
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

internal static class NavidromeClientTestExtensions
{
    /// <summary>Ping variant that surfaces the exception, to assert the failed-envelope path.</summary>
    public static async Task PingAsyncThrowing(this INavidromeClient client, CancellationToken ct)
    {
        // GetStarredSongsAsync goes through the same SendAsync envelope check as ping but rethrows.
        await client.GetStarredSongsAsync(ct);
    }
}
