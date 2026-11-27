using MusicHoarder.Api.Catalog;

namespace MusicHoarder.Api.Tests.Catalog;

public class CatalogGuardsTests
{
    [Theory]
    [InlineData("https://cdnt-preview.dzcdn.net/stream/abc.mp3?hdnea=x", true)]
    [InlineData("https://dzcdn.net/a.mp3", true)]
    [InlineData("https://CDN-PREVIEW.DZCDN.NET/a.mp3", true)]
    [InlineData("http://cdnt-preview.dzcdn.net/a.mp3", false)]
    [InlineData("https://dzcdn.net.evil.example/a.mp3", false)]
    [InlineData("https://evildzcdn.net/a.mp3", false)]
    [InlineData("https://dzcdn.net@evil.example/a.mp3", false)]
    [InlineData("https://evil.example/dzcdn.net/a.mp3", false)]
    [InlineData("https://cdn.dzcdn.net:8443/a.mp3", false)]
    [InlineData("https://127.0.0.1/a.mp3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void DeezerCdn_OnlyAcceptsHttpsDzcdnHosts(string? url, bool allowed) =>
        Assert.Equal(allowed, CatalogHosts.IsDeezerCdn(url));

    [Theory]
    [InlineData("https://rr1---sn-32o-1the.googlevideo.com/videoplayback?expire=1", true)]
    [InlineData("https://googlevideo.com/videoplayback", true)]
    [InlineData("http://rr1.googlevideo.com/videoplayback", false)]
    [InlineData("https://googlevideo.com.evil.example/videoplayback", false)]
    [InlineData("https://notgooglevideo.com/videoplayback", false)]
    [InlineData("https://www.youtube.com/watch?v=x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData(null, false)]
    public void GoogleVideo_OnlyAcceptsHttpsGooglevideoHosts(string? url, bool allowed) =>
        Assert.Equal(allowed, CatalogHosts.IsGoogleVideo(url));

    [Theory]
    [InlineData("3135556", true)]
    [InlineData("0", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("12a", false)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("../etc", false)]
    [InlineData("12345678901234567890", false)]
    public void DeezerIds_AreDigitsOnly(string? id, bool valid) =>
        Assert.Equal(valid, CatalogIds.IsValid(id));
}
