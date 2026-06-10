using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using SkiaSharp;

namespace MusicHoarder.Api.Tests.Artwork;

public sealed class CoverThumbnailServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mh-thumb-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetThumbnail_ResizesLargeCover_ToSmallWebp()
    {
        var cacheDir = Path.Combine(_dir, "cache");
        var sourcePng = await WritePngAsync("big.png", 1000, 800);
        var service = new CoverThumbnailService(cacheDir, NullLogger<CoverThumbnailService>.Instance);

        var thumb = await service.GetThumbnailAsync(FileCover(sourcePng), sourcePng, 128);

        Assert.NotNull(thumb);
        Assert.Equal("image/webp", thumb!.ContentType);
        Assert.NotNull(thumb.FilePath);
        Assert.True(File.Exists(thumb.FilePath));

        var info = Identify(thumb.FilePath!);
        Assert.Equal(SKEncodedImageFormat.Webp, info.Format);
        Assert.True(info.Width <= 128 && info.Height <= 128, $"thumb {info.Width}x{info.Height} exceeds 128");
        // Aspect preserved (1000x800 → max edge 128).
        Assert.Equal(128, info.Width);

        Assert.True(new FileInfo(thumb.FilePath!).Length < new FileInfo(sourcePng).Length);
    }

    [Fact]
    public async Task GetThumbnail_SecondCall_HitsCache_NoNewFile()
    {
        var cacheDir = Path.Combine(_dir, "cache");
        var sourcePng = await WritePngAsync("a.png", 600, 600);
        var service = new CoverThumbnailService(cacheDir, NullLogger<CoverThumbnailService>.Instance);

        var first = await service.GetThumbnailAsync(FileCover(sourcePng), sourcePng, 256);
        var second = await service.GetThumbnailAsync(FileCover(sourcePng), sourcePng, 256);

        Assert.Equal(first!.FilePath, second!.FilePath);
        Assert.Single(Directory.EnumerateFiles(cacheDir, "*.webp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GetThumbnail_SmallerOriginal_IsNotUpscaled()
    {
        var cacheDir = Path.Combine(_dir, "cache");
        var sourcePng = await WritePngAsync("tiny.png", 64, 64);
        var service = new CoverThumbnailService(cacheDir, NullLogger<CoverThumbnailService>.Instance);

        var thumb = await service.GetThumbnailAsync(FileCover(sourcePng), sourcePng, 128);

        var info = Identify(thumb!.FilePath!);
        Assert.Equal(64, info.Width);
        Assert.Equal(64, info.Height);
    }

    [Fact]
    public async Task GetThumbnail_FromEmbeddedBytes_ProducesWebp()
    {
        var cacheDir = Path.Combine(_dir, "cache");
        var bytes = await PngBytesAsync(500, 500);
        var identity = await WritePngAsync("audio-stand-in.png", 10, 10); // any existing path for the mtime key
        var service = new CoverThumbnailService(cacheDir, NullLogger<CoverThumbnailService>.Instance);

        var thumb = await service.GetThumbnailAsync(
            new ResolvedCover { Bytes = bytes, ContentType = "image/png" }, identity, 256);

        Assert.NotNull(thumb);
        var info = Identify(thumb!.FilePath!);
        Assert.Equal(SKEncodedImageFormat.Webp, info.Format);
        Assert.True(info.Width <= 256 && info.Height <= 256);
    }

    [Theory]
    [InlineData(50, 128)]
    [InlineData(176, 256)]
    [InlineData(256, 256)]
    [InlineData(700, 640)]
    public void ClampToBucket_RoundsUpToNearestBucket(int requested, int expected)
    {
        var service = new CoverThumbnailService(_dir, NullLogger<CoverThumbnailService>.Instance);
        Assert.Equal(expected, service.ClampToBucket(requested));
    }

    private static ResolvedCover FileCover(string path) => new() { FilePath = path, ContentType = "image/png" };

    private async Task<string> WritePngAsync(string name, int w, int h)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(path, await PngBytesAsync(w, h));
        return path;
    }

    private static Task<byte[]> PngBytesAsync(int w, int h)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(new SKColor(70, 130, 180));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Task.FromResult(data.ToArray());
    }

    private static (SKEncodedImageFormat Format, int Width, int Height) Identify(string path)
    {
        using var codec = SKCodec.Create(path);
        return (codec.EncodedFormat, codec.Info.Width, codec.Info.Height);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
