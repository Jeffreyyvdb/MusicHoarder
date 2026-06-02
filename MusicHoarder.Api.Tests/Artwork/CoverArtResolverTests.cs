using System.IO.Abstractions.TestingHelpers;
using MusicHoarder.Api.Artwork;

namespace MusicHoarder.Api.Tests.Artwork;

public class CoverArtResolverTests
{
    private const string Track = "/music/Album/track.mp3";

    [Fact]
    public void Resolve_PrefersFolderImage_OverEmbedded()
    {
        var jpg = new byte[] { 0xFF, 0xD8, 0xFF, 1 };
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Track] = new("audio"),
            ["/music/Album/cover.jpg"] = new(jpg)
        });
        var resolver = new CoverArtResolver(fs, new StubReader(new EmbeddedPicture([0x89, 0x50, 0x4E, 0x47], "image/png")));

        var cover = resolver.Resolve(Track);

        Assert.NotNull(cover);
        Assert.Equal("/music/Album/cover.jpg", cover!.FilePath);
        Assert.Equal("image/jpeg", cover.ContentType);
        Assert.Null(cover.Bytes);
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("Folder.PNG")]
    [InlineData("FRONT.webp")]
    public void Resolve_MatchesCoverNames_CaseInsensitively(string fileName)
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Track] = new("audio"),
            [$"/music/Album/{fileName}"] = new(new byte[] { 1, 2, 3 })
        });
        var resolver = new CoverArtResolver(fs, new StubReader(null));

        var cover = resolver.Resolve(Track);

        Assert.NotNull(cover);
        Assert.Equal($"/music/Album/{fileName}", cover!.FilePath);
    }

    [Fact]
    public void Resolve_AppliesNamePriority_CoverBeatsFolderBeatsFront()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Track] = new("audio"),
            ["/music/Album/front.png"] = new(new byte[] { 1 }),
            ["/music/Album/folder.png"] = new(new byte[] { 2 }),
            ["/music/Album/cover.png"] = new(new byte[] { 3 })
        });
        var resolver = new CoverArtResolver(fs, new StubReader(null));

        Assert.Equal("/music/Album/cover.png", resolver.Resolve(Track)!.FilePath);
    }

    [Fact]
    public void Resolve_FallsBackToEmbedded_WhenNoFolderImage()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 5, 6 };
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Track] = new("audio")
        });
        var resolver = new CoverArtResolver(fs, new StubReader(new EmbeddedPicture(png, "image/png")));

        var cover = resolver.Resolve(Track);

        Assert.NotNull(cover);
        Assert.Equal(png, cover!.Bytes);
        Assert.Equal("image/png", cover.ContentType);
        Assert.Null(cover.FilePath);
    }

    [Fact]
    public void Resolve_SniffsMime_WhenEmbeddedMimeIsBogus()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Track] = new("audio") });
        var resolver = new CoverArtResolver(fs, new StubReader(new EmbeddedPicture(png, "")));

        Assert.Equal("image/png", resolver.Resolve(Track)!.ContentType);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNeitherSourceHasArt()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Track] = new("audio") });
        var resolver = new CoverArtResolver(fs, new StubReader(null));

        Assert.Null(resolver.Resolve(Track));
    }

    [Fact]
    public void DirectoryHasCoverImage_TrueOnlyWhenAMatchingImageExists()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/a/cover.jpg"] = new("x"),
            ["/b/notes.txt"] = new("x"),
            ["/b/art.jpg"] = new("x")
        });
        var resolver = new CoverArtResolver(fs, new StubReader(null));

        Assert.True(resolver.DirectoryHasCoverImage("/a"));
        Assert.False(resolver.DirectoryHasCoverImage("/b"));
        Assert.False(resolver.DirectoryHasCoverImage("/missing"));
        Assert.False(resolver.DirectoryHasCoverImage(null));
    }

    private sealed class StubReader(EmbeddedPicture? picture) : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => picture;
    }
}
