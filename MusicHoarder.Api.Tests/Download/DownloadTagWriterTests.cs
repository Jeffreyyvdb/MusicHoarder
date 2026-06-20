using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Round-trips real audio files through <see cref="DownloadTagWriter"/> (TagLib hits the real
/// filesystem, so these use temp copies of the committed silent fixtures). The stamper is what
/// replaces the downloader's poisoned YouTube tags with the known Spotify identity before the scanner
/// reads them.
/// </summary>
public class DownloadTagWriterTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly List<string> tempFiles = [];

    [Fact]
    public void Stamp_OverwritesArtistTitleAlbumAndIsrc()
    {
        var path = CopyFixture("silence.mp3");
        // Seed the file with junk yt-dlp-style tags to prove they get overwritten.
        using (var seed = TagLib.File.Create(path))
        {
            seed.Tag.Performers = ["현장검거"]; // YouTube channel
            seed.Tag.Title = "¥$, Kanye West & Ty Dolla $ign - PROBLEMATIC [가사/한글자막/해석]";
            seed.Save();
        }

        var ok = DownloadTagWriter.Stamp(path, "¥$", "PROBLEMATIC", "VULTURES 1", "USUG12400001");

        Assert.True(ok);
        using var file = TagLib.File.Create(path);
        Assert.Equal(["¥$"], file.Tag.Performers);
        Assert.Equal("PROBLEMATIC", file.Tag.Title);
        Assert.Equal("VULTURES 1", file.Tag.Album);
        Assert.Equal("USUG12400001", file.Tag.ISRC);
    }

    [Fact]
    public void Stamp_DerivesAlbumArtistFromPrimaryCredit()
    {
        // AlbumArtist must be the bare primary so a multi-artist credit never splits the album —
        // mirrors how the scanner/library writer derive it via ArtistCreditNormalizer.
        var path = CopyFixture("silence.flac");

        var ok = DownloadTagWriter.Stamp(path, "Tyler, The Creator & Kali Uchis", "See You Again", "Flower Boy", null);

        Assert.True(ok);
        using var file = TagLib.File.Create(path);
        Assert.Equal("See You Again", file.Tag.Title);
        // "Tyler, The Creator" is preserved as the primary (comma is part of the name, not a separator).
        Assert.Equal(["Tyler, The Creator"], file.Tag.AlbumArtists);
    }

    [Fact]
    public void Stamp_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mh-missing-{Guid.NewGuid():N}.opus");

        var ok = DownloadTagWriter.Stamp(path, "Artist", "Title", "Album", null);

        Assert.False(ok);
    }

    [Fact]
    public void Stamp_NullAlbumAndIsrc_LeavesThemEmptyNotThrows()
    {
        var path = CopyFixture("silence.mp3");

        var ok = DownloadTagWriter.Stamp(path, "Amy Macdonald", "This Is the Life", null, null);

        Assert.True(ok);
        using var file = TagLib.File.Create(path);
        Assert.Equal("This Is the Life", file.Tag.Title);
        Assert.True(string.IsNullOrEmpty(file.Tag.Album));
        Assert.True(string.IsNullOrEmpty(file.Tag.ISRC));
    }

    private string CopyFixture(string fixtureName)
    {
        var source = Path.Combine(FixtureDir, fixtureName);
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"mh-downloadtag-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
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
