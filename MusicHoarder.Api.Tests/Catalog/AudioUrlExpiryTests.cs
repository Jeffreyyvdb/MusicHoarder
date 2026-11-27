using MusicHoarder.Api.Catalog;

namespace MusicHoarder.Api.Tests.Catalog;

public class AudioUrlExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static string UrlExpiringIn(TimeSpan span) =>
        $"https://rr1.googlevideo.com/videoplayback?id=1&expire={Now.Add(span).ToUnixTimeSeconds()}&mime=audio%2Fmp4";

    [Fact]
    public void LivesUntilShortlyBeforeTheUrlExpires()
    {
        var lifetime = AudioUrlExpiry.CacheLifetime(UrlExpiringIn(TimeSpan.FromMinutes(30)), Now);
        Assert.Equal(TimeSpan.FromMinutes(28), lifetime);
    }

    [Fact]
    public void IsCappedAtAnHour_WhenTheUrlLivesForHours()
    {
        var lifetime = AudioUrlExpiry.CacheLifetime(UrlExpiringIn(TimeSpan.FromHours(6)), Now);
        Assert.Equal(TimeSpan.FromHours(1), lifetime);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(0)]
    [InlineData(-3600)]
    public void IsNotKept_WhenTheUrlIsAboutToLapse(int secondsLeft)
    {
        var lifetime = AudioUrlExpiry.CacheLifetime(UrlExpiringIn(TimeSpan.FromSeconds(secondsLeft)), Now);
        Assert.Equal(TimeSpan.Zero, lifetime);
    }

    [Theory]
    [InlineData("https://rr1.googlevideo.com/videoplayback?id=1")]
    [InlineData("https://rr1.googlevideo.com/videoplayback?expire=soon")]
    [InlineData("https://rr1.googlevideo.com/videoplayback?expire=99999999999999999")]
    [InlineData("not a url")]
    public void FallsBackToAShortLifetime_WhenThereIsNoReadableExpiry(string url) =>
        Assert.Equal(AudioUrlExpiry.UnknownExpiryLifetime, AudioUrlExpiry.CacheLifetime(url, Now));

    [Fact]
    public void ContentType_ComesFromTheUrlsMimeParameter()
    {
        Assert.Equal("audio/mp4", YtDlpAudioLocator.ContentTypeOf("https://x.googlevideo.com/v?mime=audio%2Fmp4"));
        Assert.Equal("audio/webm", YtDlpAudioLocator.ContentTypeOf("https://x.googlevideo.com/v?mime=audio%2FWebM"));
        Assert.Equal("audio/mp4", YtDlpAudioLocator.ContentTypeOf("https://x.googlevideo.com/v?mime=video%2Fmp4"));
        Assert.Equal("audio/mp4", YtDlpAudioLocator.ContentTypeOf("https://x.googlevideo.com/v"));
    }
}
