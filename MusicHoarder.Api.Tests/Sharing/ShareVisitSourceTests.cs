using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Tests.Sharing;

public class ShareVisitSourceTests
{
    private const string Safari =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    [Theory]
    [InlineData("https://www.tiktok.com/", "TikTok")]
    [InlineData("https://vm.tiktok.com/ZMabc/", "TikTok")]
    [InlineData("https://l.instagram.com/?u=x", "Instagram")]
    [InlineData("https://lm.facebook.com/", "Facebook")]
    [InlineData("https://t.co/abc", "X")]
    [InlineData("https://x.com/someone/status/1", "X")]
    [InlineData("https://www.youtube.com/watch?v=1", "YouTube")]
    [InlineData("https://out.reddit.com/t3_x", "Reddit")]
    [InlineData("https://www.google.nl/", "Google")]
    [InlineData("https://www.google.co.uk/", "Google")]
    [InlineData("android-app://com.zhiliaoapp.musically/", "TikTok")]
    [InlineData("android-app://com.whatsapp/", "WhatsApp")]
    [InlineData("android-app://org.example.reader/", "org.example.reader")]
    [InlineData("https://www.someblog.example/post", "someblog.example")]
    public void Names_the_referring_site_or_app(string referrer, string expected)
    {
        Assert.Equal(expected, ShareVisitSource.Classify(referrer, Safari));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 musical_ly_36.5.0 JsSdk/2.0 NetType/WIFI Channel/App Store ByteLocale/en Region/NL", "TikTok")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/126.0 Mobile Safari/537.36 trill_360503 BytedanceWebview/d8a21c6", "TikTok")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 350.0.0.0.0 (iPhone15,2; iOS 18_0; en_US)", "Instagram")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/FBIOS;FBAV/480.0.0.0]", "Facebook")]
    public void Falls_back_to_the_in_app_browser_when_there_is_no_referrer(string userAgent, string expected)
    {
        Assert.Equal(expected, ShareVisitSource.Classify(referrer: null, userAgent));
    }

    [Fact]
    public void The_referrer_wins_over_the_in_app_browser()
    {
        const string instagramApp = "Mozilla/5.0 (iPhone) AppleWebKit/605.1.15 Mobile/15E148 Instagram 350.0";
        Assert.Equal("TikTok", ShareVisitSource.Classify("https://www.tiktok.com/", instagramApp));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    public void Says_nothing_when_nothing_says_anything(string? referrer)
    {
        Assert.Null(ShareVisitSource.Classify(referrer, Safari));
    }

    [Fact]
    public void Clips_long_hosts_to_the_column_width()
    {
        var host = new string('a', 80) + ".example";
        var source = ShareVisitSource.Classify($"https://{host}/", Safari);
        Assert.NotNull(source);
        Assert.Equal(64, source.Length);
    }

    [Theory]
    [InlineData(Safari, false)]
    [InlineData("okhttp/4.12.0", false)] // the Android client's share viewer
    [InlineData("Twitterbot/1.0", true)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", true)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/126.0 Safari/537.36", true)]
    [InlineData("WhatsApp/2.2329.7 A", true)] // its link-preview fetcher; the app has no in-app browser
    public void Tells_people_from_machines(string userAgent, bool isBot)
    {
        Assert.Equal(isBot, ShareVisitSource.IsLikelyBot(userAgent));
    }
}
