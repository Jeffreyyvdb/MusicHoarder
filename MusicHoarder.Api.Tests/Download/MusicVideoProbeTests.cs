using System.Text.Json;
using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// The classification thresholds are calibrated against real YouTube uploads, so the band tests
/// below use the values actually measured from them rather than round numbers — a change that moves
/// a real upload into the wrong band should fail here.
/// </summary>
public class MusicVideoProbeTests
{
    // Measured: mean absolute luma delta between consecutive storyboard frames, whole runtime.
    [Theory]
    // Album-cover uploads: a "- Topic" art track, two label uploads, an "(Audio)" upload.
    [InlineData(0.00, 5.65, MusicVideoMotion.Static)]
    [InlineData(0.00, 7.53, MusicVideoMotion.Static)]
    [InlineData(0.00, 1.48, MusicVideoMotion.Static)]
    [InlineData(0.33, 12.88, MusicVideoMotion.Static)]
    // Lyric video: mostly still, hard cuts between lyric cards.
    [InlineData(3.38, 49.78, MusicVideoMotion.LowMotion)]
    // Real music videos.
    [InlineData(24.57, 115.51, MusicVideoMotion.RealVideo)]
    [InlineData(26.83, 82.90, MusicVideoMotion.RealVideo)]
    [InlineData(38.03, 117.87, MusicVideoMotion.RealVideo)]
    [InlineData(42.40, 90.18, MusicVideoMotion.RealVideo)]
    [InlineData(14.80, 49.89, MusicVideoMotion.RealVideo)]
    public void Classify_PutsMeasuredUploadsInTheRightBand(double median, double max, MusicVideoMotion expected)
    {
        Assert.Equal(expected, MusicVideoProbe.Classify(median, max));
    }

    [Fact]
    public void Classify_StillImageWithHardCuts_IsSlideshowNotStatic()
    {
        // Two stills spliced together: almost every frame pair is identical, but the cut is huge.
        // That is a slideshow, and calling it Static would let it through the "one image" filter.
        Assert.Equal(MusicVideoMotion.LowMotion, MusicVideoProbe.Classify(0.0, 90.0));
    }

    [Fact]
    public void SelectMotionSheet_PrefersWholeRuntimeCoverageOverResolution()
    {
        var sheets = new List<MusicVideoProbe.Storyboard>
        {
            new("sb0", 320, 180, 3, 3, 0.03, ["a"]),          // 9 frames, 1 fragment
            new("sb1", 160, 90, 5, 5, 0.1, ["a", "b", "c"]),  // more frames but 3 requests
            new("sb3", 48, 27, 10, 10, 0.36, ["a"]),          // 100 frames, 1 fragment
        };

        var selected = MusicVideoProbe.SelectMotionSheet(sheets);

        Assert.Equal("sb3", selected!.FormatId);
    }

    [Fact]
    public void SelectMotionSheet_IgnoresSheetsWithoutUsableGrid()
    {
        var sheets = new List<MusicVideoProbe.Storyboard>
        {
            new("sb0", 320, 180, 1, 1, null, ["a"]), // a single frame cannot yield a delta
            new("sb1", 160, 90, 2, 2, null, ["a"]),
        };

        Assert.Equal("sb1", MusicVideoProbe.SelectMotionSheet(sheets)!.FormatId);
    }

    [Fact]
    public void SelectMotionSheet_NoStoryboards_ReturnsNull()
    {
        Assert.Null(MusicVideoProbe.SelectMotionSheet([]));
    }

    [Fact]
    public void IsSquareSource_ReadsTheHighestResolutionSheet()
    {
        // YouTube letterboxes its smallest storyboard levels into 16:9, so only the large sheets
        // still show that the upload itself is a square album cover.
        var sheets = new List<MusicVideoProbe.Storyboard>
        {
            new("sb2", 48, 27, 10, 10, null, ["a"]),
            new("sb0", 90, 90, 5, 5, null, ["a"]),
        };

        Assert.True(MusicVideoProbe.IsSquareSource(sheets));
    }

    [Fact]
    public void IsSquareSource_WidescreenUpload_IsFalse()
    {
        Assert.False(MusicVideoProbe.IsSquareSource([new("sb0", 320, 180, 3, 3, null, ["a"])]));
    }

    [Fact]
    public void UsableTiles_ClampsPaddingOnAPartiallyFilledSheet()
    {
        // 5x5 grid, but a 40 s video at 0.2 fps only fills 8 of the 25 tiles — the rest is padding.
        var sheet = new MusicVideoProbe.Storyboard("sb1", 45, 45, 5, 5, 0.2, ["a"]);

        Assert.Equal(8, MusicVideoProbe.UsableTiles(sheet, 40));
    }

    [Fact]
    public void UsableTiles_NoFps_UsesTheWholeGrid()
    {
        var sheet = new MusicVideoProbe.Storyboard("sb1", 45, 45, 5, 5, null, ["a"]);

        Assert.Equal(25, MusicVideoProbe.UsableTiles(sheet, 40));
    }

    [Fact]
    public void MeasureFrames_IdenticalTiles_ReportsNoMotion()
    {
        var sheet = Sheet(rows: 2, columns: 2, tile: 4, tileValue: _ => 120);

        var measured = MusicVideoProbe.MeasureFrames(sheet, 8, 8, 2, 2, 4, 4, usableTiles: 4);

        Assert.NotNull(measured);
        Assert.Equal(0, measured!.Value.Median);
        Assert.Equal(0, measured.Value.Max);
    }

    [Fact]
    public void MeasureFrames_AlternatingTiles_ReportsTheFullSwing()
    {
        var sheet = Sheet(rows: 2, columns: 2, tile: 4, tileValue: i => i % 2 == 0 ? (byte)0 : (byte)200);

        var measured = MusicVideoProbe.MeasureFrames(sheet, 8, 8, 2, 2, 4, 4, usableTiles: 4);

        Assert.Equal(200, measured!.Value.Median);
        Assert.Equal(200, measured.Value.Max);
    }

    [Fact]
    public void MeasureFrames_StopsAtUsableTiles_SoPaddingIsNotReadAsStillness()
    {
        // Two real frames that differ, then two black padding tiles. Counting the padding would
        // drag the median to zero and mislabel a moving video as a static cover.
        var sheet = Sheet(rows: 2, columns: 2, tile: 4, tileValue: i => i switch
        {
            0 => 0,
            1 => 200,
            _ => 0,
        });

        var measured = MusicVideoProbe.MeasureFrames(sheet, 8, 8, 2, 2, 4, 4, usableTiles: 2);

        Assert.Equal(200, measured!.Value.Median);
    }

    [Fact]
    public void MeasureFrames_GridDoesNotFitTheDecodedImage_ReturnsNull()
    {
        var sheet = Sheet(rows: 2, columns: 2, tile: 4, tileValue: _ => 10);

        Assert.Null(MusicVideoProbe.MeasureFrames(sheet, 8, 8, 3, 3, 4, 4, usableTiles: 9));
    }

    [Fact]
    public void MeasureFrames_PixelCountMismatch_ReturnsNull()
    {
        Assert.Null(MusicVideoProbe.MeasureFrames(new byte[10], 8, 8, 2, 2, 4, 4, usableTiles: 4));
    }

    /// <summary>Builds a row-major grayscale sprite sheet whose every tile is one flat value.</summary>
    private static byte[] Sheet(int rows, int columns, int tile, Func<int, byte> tileValue)
    {
        var width = columns * tile;
        var pixels = new byte[width * rows * tile];
        for (var index = 0; index < rows * columns; index++)
        {
            var value = tileValue(index);
            var top = index / columns * tile;
            var left = index % columns * tile;
            for (var y = 0; y < tile; y++)
                for (var x = 0; x < tile; x++)
                    pixels[(top + y) * width + left + x] = value;
        }
        return pixels;
    }

    [Fact]
    public void ParseStoryboards_KeepsOnlyUsableStoryboardFormats()
    {
        var json = """
        [
          { "format_id": "137", "ext": "mp4", "height": 1080, "vcodec": "avc1" },
          { "format_id": "sb0", "width": 320, "height": 180, "rows": 3, "columns": 3, "fps": 0.05,
            "fragments": [{ "url": "https://example.test/sb0.jpg" }] },
          { "format_id": "sb1", "width": 160, "height": 90, "rows": 5, "columns": 5 },
          { "format_id": "sb2", "width": 0, "height": 0, "rows": 5, "columns": 5,
            "fragments": [{ "url": "https://example.test/sb2.jpg" }] }
        ]
        """;

        var storyboards = MusicVideoProbe.ParseStoryboards(JsonDocument.Parse(json).RootElement);

        var only = Assert.Single(storyboards);
        Assert.Equal("sb0", only.FormatId);
        Assert.Equal(0.05, only.Fps);
        Assert.Equal(["https://example.test/sb0.jpg"], only.FragmentUrls);
    }

    [Fact]
    public void EstimateDownloadBytes_AddsBestCappedVideoToBestAudio()
    {
        var json = """
        [
          { "format_id": "137", "ext": "mp4", "height": 1080, "vcodec": "avc1", "acodec": "none", "filesize": 63652523 },
          { "format_id": "136", "ext": "mp4", "height": 720,  "vcodec": "avc1", "acodec": "none", "filesize": 28348580 },
          { "format_id": "140", "ext": "m4a", "height": 0,    "vcodec": "none", "acodec": "mp4a", "filesize": 4000000 }
        ]
        """;

        var estimate = MusicVideoProbe.EstimateDownloadBytes(
            JsonDocument.Parse(json).RootElement, maxHeight: 720, durationSeconds: 278);

        Assert.Equal(28348580 + 4000000, estimate);
    }

    [Fact]
    public void EstimateDownloadBytes_NoReportedSize_FallsBackToBitrateTimesDuration()
    {
        var json = """
        [
          { "format_id": "136", "ext": "mp4", "height": 720, "vcodec": "avc1", "acodec": "none", "vbr": 800 }
        ]
        """;

        var estimate = MusicVideoProbe.EstimateDownloadBytes(
            JsonDocument.Parse(json).RootElement, maxHeight: 1080, durationSeconds: 100);

        Assert.Equal(800L * 1000 / 8 * 100, estimate);
    }

    [Fact]
    public void EstimateDownloadBytes_NoVideoWithinTheHeightCap_ReturnsNull()
    {
        var json = """
        [
          { "format_id": "137", "ext": "mp4", "height": 1080, "vcodec": "avc1", "acodec": "none", "filesize": 1 }
        ]
        """;

        Assert.Null(MusicVideoProbe.EstimateDownloadBytes(
            JsonDocument.Parse(json).RootElement, maxHeight: 720, durationSeconds: 100));
    }

    [Fact]
    public void ParseMetadata_ReadsTitleChannelDurationAndThumbnail()
    {
        var json = """
        {
          "title": "Artist - Song (Official Video)",
          "channel": "ArtistVEVO",
          "duration": 278.4,
          "thumbnails": [
            { "url": "https://example.test/small.jpg", "width": 120 },
            { "url": "https://example.test/large.jpg", "width": 1280 }
          ],
          "formats": []
        }
        """;

        var metadata = MusicVideoProbe.ParseMetadata(json, maxHeight: 1080);

        Assert.Equal("Artist - Song (Official Video)", metadata!.Title);
        Assert.Equal("ArtistVEVO", metadata.Channel);
        Assert.Equal(278, metadata.DurationSeconds);
        Assert.Equal("https://example.test/large.jpg", metadata.ThumbnailUrl);
    }

    [Fact]
    public void ParseMetadata_Garbage_ReturnsNull()
    {
        Assert.Null(MusicVideoProbe.ParseMetadata("not json", maxHeight: 1080));
    }
}
