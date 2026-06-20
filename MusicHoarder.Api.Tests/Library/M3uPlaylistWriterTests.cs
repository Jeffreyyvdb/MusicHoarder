using System.Text;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Library;

public class M3uPlaylistWriterTests
{
    [Fact]
    public void BuildRelativePath_FromPlaylistsSubfolder_UsesParentRelativeForwardSlashes()
    {
        var rel = M3uPlaylistWriter.BuildRelativePath(
            playlistsDir: Path.Combine("/music", "Playlists"),
            destinationPath: Path.Combine("/music", "Tyler, The Creator", "2021 - Call Me If You Get Lost", "01 - Sir Baudelaire.flac"));

        Assert.Equal("../Tyler, The Creator/2021 - Call Me If You Get Lost/01 - Sir Baudelaire.flac", rel);
    }

    [Fact]
    public void BuildContent_EmitsExtendedHeaderAndExtinfPerTrackInOrder()
    {
        var playlistsDir = Path.Combine("/music", "Playlists");
        var entries = new List<M3uEntry>
        {
            new(Path.Combine("/music", "A", "Album", "01 - First.flac"), "Artist A", "First", 215),
            new(Path.Combine("/music", "B", "Album", "02 - Second.flac"), "Artist B", "Second", 180),
        };

        var content = M3uPlaylistWriter.BuildContent(playlistsDir, entries);
        var lines = content.Split('\n');

        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#EXTINF:215,Artist A - First", lines[1]);
        Assert.Equal("../A/Album/01 - First.flac", lines[2]);
        Assert.Equal("#EXTINF:180,Artist B - Second", lines[3]);
        Assert.Equal("../B/Album/02 - Second.flac", lines[4]);
    }

    [Fact]
    public void BuildContent_WithUnknownDuration_UsesMinusOne()
    {
        var content = M3uPlaylistWriter.BuildContent(
            Path.Combine("/music", "Playlists"),
            [new M3uEntry(Path.Combine("/music", "A", "x.flac"), "A", "X", null)]);

        Assert.Contains("#EXTINF:-1,A - X", content);
    }

    [Fact]
    public async Task WriteAsync_WritesUtf8WithoutBom_AndCreatesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "mh-m3u-test-" + Guid.NewGuid().ToString("N"));
        var playlistsDir = Path.Combine(root, "Playlists");
        var filePath = Path.Combine(playlistsDir, "Liked Songs.m3u8");
        try
        {
            var writer = new M3uPlaylistWriter();
            await writer.WriteAsync(filePath, playlistsDir,
                [new M3uEntry(Path.Combine(root, "Bjork", "Album", "01 - Jóga.flac"), "Björk", "Jóga", 303)]);

            Assert.True(File.Exists(filePath));
            // No leftover temp file.
            Assert.False(File.Exists(filePath + ".tmp"));

            var bytes = await File.ReadAllBytesAsync(filePath);
            // No UTF-8 BOM (EF BOM is 0xEF,0xBB,0xBF).
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            var text = Encoding.UTF8.GetString(bytes);
            Assert.StartsWith("#EXTM3U\n", text);
            Assert.Contains("#EXTINF:303,Björk - Jóga", text);
            Assert.Contains("../Bjork/Album/01 - Jóga.flac", text);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "mh-m3u-test-" + Guid.NewGuid().ToString("N"));
        var playlistsDir = Path.Combine(root, "Playlists");
        var filePath = Path.Combine(playlistsDir, "P.m3u8");
        try
        {
            var writer = new M3uPlaylistWriter();
            await writer.WriteAsync(filePath, playlistsDir,
                [new M3uEntry(Path.Combine(root, "A", "old.flac"), "A", "Old", 100)]);
            await writer.WriteAsync(filePath, playlistsDir,
                [new M3uEntry(Path.Combine(root, "A", "new.flac"), "A", "New", 100)]);

            var text = await File.ReadAllTextAsync(filePath);
            Assert.Contains("../A/new.flac", text);
            Assert.DoesNotContain("old.flac", text);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
