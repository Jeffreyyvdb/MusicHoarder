using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;

namespace MusicHoarder.Api.Tests.Artwork;

/// <summary>
/// Exercises the real TagLib-backed reader against temp copies of the committed silent fixtures
/// (TagLib hits the real filesystem). Pictures are seeded in-test so no new binary fixture is needed.
/// </summary>
public class TagLibEmbeddedPictureReaderTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly List<string> tempFiles = [];
    private readonly TagLibEmbeddedPictureReader reader = new(NullLogger<TagLibEmbeddedPictureReader>.Instance);

    [Fact]
    public void ReadFront_ReturnsNull_WhenNoEmbeddedPicture()
    {
        var path = CopyFixture("silence.mp3");
        Assert.Null(reader.ReadFront(path));
    }

    [Fact]
    public void ReadFront_ReturnsEmbeddedPicture()
    {
        var path = CopyFixture("silence.mp3");
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 };
        EmbedPictures(path, [(bytes, "image/png", TagLib.PictureType.FrontCover)]);

        var picture = reader.ReadFront(path);

        Assert.NotNull(picture);
        Assert.Equal(bytes, picture!.Data);
        Assert.Equal("image/png", picture.MimeType);
    }

    [Fact]
    public void ReadFront_PrefersFrontCover_OverOtherPictures()
    {
        var path = CopyFixture("silence.mp3");
        var other = new byte[] { 1, 1, 1 };
        var front = new byte[] { 2, 2, 2 };
        EmbedPictures(path,
        [
            (other, "image/png", TagLib.PictureType.Other),
            (front, "image/png", TagLib.PictureType.FrontCover),
        ]);

        Assert.Equal(front, reader.ReadFront(path)!.Data);
    }

    private static void EmbedPictures(string path, (byte[] Bytes, string Mime, TagLib.PictureType Type)[] pictures)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Pictures = pictures
            .Select(p => (TagLib.IPicture)new TagLib.Picture(new TagLib.ByteVector(p.Bytes))
            {
                MimeType = p.Mime,
                Type = p.Type,
            })
            .ToArray();
        file.Save();
    }

    private string CopyFixture(string fixtureName)
    {
        var source = Path.Combine(FixtureDir, fixtureName);
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"mh-pictest-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
        File.Copy(source, dest, overwrite: true);
        tempFiles.Add(dest);
        return dest;
    }

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}
