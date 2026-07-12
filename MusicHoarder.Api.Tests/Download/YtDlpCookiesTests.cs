using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

public class YtDlpCookiesTests
{
    [Fact]
    public void PrepareWritableCopy_CopiesToWritableTemp_ThenCleansUp()
    {
        var src = Path.Combine(Path.GetTempPath(), $"mh-cookies-src-{Guid.NewGuid():N}.txt");
        File.WriteAllText(src, "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tX\ty\n");
        try
        {
            var copy = YtDlpCookies.PrepareWritableCopy(src, NullLogger.Instance);

            Assert.NotNull(copy);
            Assert.NotEqual(src, copy);
            Assert.True(File.Exists(copy));
            Assert.Equal(File.ReadAllText(src), File.ReadAllText(copy!));

            YtDlpCookies.Cleanup(copy, src);
            Assert.False(File.Exists(copy));
            Assert.True(File.Exists(src)); // original untouched
        }
        finally
        {
            File.Delete(src);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/no/such/cookies.txt")]
    public void PrepareWritableCopy_ReturnsNull_WhenUnsetOrMissing(string? path)
    {
        Assert.Null(YtDlpCookies.PrepareWritableCopy(path, NullLogger.Instance));
    }

    [Fact]
    public void Cleanup_DoesNotDeleteConfiguredFile_WhenCopyFellBack()
    {
        var src = Path.Combine(Path.GetTempPath(), $"mh-cookies-fallback-{Guid.NewGuid():N}.txt");
        File.WriteAllText(src, "cookies");
        try
        {
            // When prepared == configured (fallback path), Cleanup must be a no-op.
            YtDlpCookies.Cleanup(src, src);
            Assert.True(File.Exists(src));
        }
        finally
        {
            File.Delete(src);
        }
    }
}
