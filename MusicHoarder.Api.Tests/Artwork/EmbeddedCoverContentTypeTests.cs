using System.IO.Abstractions.TestingHelpers;
using MusicHoarder.Api.Artwork;

namespace MusicHoarder.Api.Tests.Artwork;

public class EmbeddedCoverContentTypeTests
{
    private const string Track = "/music/Album/track.mp3";

    [Fact]
    public void Resolve_DropsEmbeddedPicture_WhenBytesAreNotARasterImage_EvenIfTagSaysImage()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>1</script></svg>");
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Track] = new("audio") });
        var resolver = new CoverArtResolver(fs, new StubReader(new EmbeddedPicture(svg, "image/svg+xml")));

        Assert.Null(resolver.Resolve(Track));
    }

    [Fact]
    public void Resolve_UsesSniffedType_WhenTagMimeDisagreesWithRasterBytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Track] = new("audio") });
        var resolver = new CoverArtResolver(fs, new StubReader(new EmbeddedPicture(png, "image/svg+xml")));

        Assert.Equal("image/png", resolver.Resolve(Track)!.ContentType);
    }

    private sealed class StubReader(EmbeddedPicture? picture) : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => picture;
    }
}
