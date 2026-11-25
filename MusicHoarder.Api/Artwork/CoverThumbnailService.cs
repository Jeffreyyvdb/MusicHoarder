using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace MusicHoarder.Api.Artwork;

public interface ICoverThumbnailService
{
    /// <summary>The on-disk cache root, so storage accounting can measure it.</summary>
    string CacheDirectory { get; }

    /// <summary>Clamps an arbitrary requested size up to the nearest supported bucket.</summary>
    int ClampToBucket(int requestedSize);

    /// <summary>
    /// Fast cache lookup by <paramref name="identityPath"/> and <paramref name="size"/> without
    /// loading or decoding the source cover (e.g. avoids opening a FLAC/MP3 with TagLib when the
    /// WebP thumbnail is already cached). Returns null on a cache miss.
    /// </summary>
    ResolvedCover? TryGetCachedThumbnail(string identityPath, int size) => null;

    /// <summary>
    /// Returns a cached WebP thumbnail of <paramref name="source"/> at <paramref name="size"/> (max
    /// edge, no upscaling), generating + caching it on first request. <paramref name="identityPath"/>
    /// is the on-disk file the cover derives from (the folder image, or the audio file for embedded
    /// art) — its last-write time keys the cache so an edited cover regenerates. Null if the source
    /// can't be decoded.
    /// </summary>
    Task<ResolvedCover?> GetThumbnailAsync(ResolvedCover source, string identityPath, int size, CancellationToken ct = default);
}

/// <summary>
/// Resizes album covers to small WebP variants and caches them on disk. The cache is a disposable,
/// content-addressed directory of derived artifacts — wiping it just makes them regenerate; the
/// originals (audio files / destination <c>cover.jpg</c>) are never touched.
/// </summary>
public sealed class CoverThumbnailService(string cacheDirectory, ILogger<CoverThumbnailService> logger) : ICoverThumbnailService
{
    private static readonly int[] BucketSizes = [128, 256, 400, 640];

    // Fixed striped lock pool: coalesces concurrent misses for the same cache file without the
    // removal races or cancellation leaks of a dynamic ConcurrentDictionary<string, SemaphoreSlim>.
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public string CacheDirectory => cacheDirectory;

    public int ClampToBucket(int requestedSize)
    {
        foreach (var b in BucketSizes)
            if (requestedSize <= b)
                return b;
        return BucketSizes[^1];
    }

    public ResolvedCover? TryGetCachedThumbnail(string identityPath, int size)
    {
        var cacheFile = CacheFileFor(identityPath, ClampToBucket(size));
        return File.Exists(cacheFile) ? Webp(cacheFile) : null;
    }

    public async Task<ResolvedCover?> GetThumbnailAsync(ResolvedCover source, string identityPath, int size, CancellationToken ct = default)
    {
        size = ClampToBucket(size);
        var cacheFile = CacheFileFor(identityPath, size);

        if (File.Exists(cacheFile))
            return Webp(cacheFile);

        var gate = _gates[(uint)StringComparer.Ordinal.GetHashCode(cacheFile) % _gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(cacheFile))
                return Webp(cacheFile);

            var bytes = source.FilePath is not null
                ? await File.ReadAllBytesAsync(source.FilePath, ct)
                : source.Bytes!;
            ct.ThrowIfCancellationRequested();

            using var original = SKBitmap.Decode(bytes);
            if (original is null)
                return null; // undecodable image

            // Fit within the box, preserving aspect; never enlarge a smaller original.
            using var resized = ResizeToFit(original, size);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var tmp = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
            using (var image = SKImage.FromBitmap(resized ?? original))
            using (var data = image.Encode(SKEncodedImageFormat.Webp, 80))
            await using (var fs = File.Create(tmp))
                data.SaveTo(fs);
            File.Move(tmp, cacheFile, overwrite: true);

            return Webp(cacheFile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cover thumbnail generation failed for {Path} @ {Size}", identityPath, size);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private string CacheFileFor(string identityPath, int clampedSize)
    {
        var stamp = SafeLastWriteTicks(identityPath);
        var hash = Hash($"{identityPath}|{stamp}|{clampedSize}");
        return Path.Combine(cacheDirectory, hash[..2], $"{hash}.webp");
    }

    /// <summary>
    /// Scales <paramref name="original"/> down to fit within a <paramref name="size"/>×<paramref name="size"/>
    /// box, preserving aspect ratio. Returns <c>null</c> when it already fits (no upscaling), letting the
    /// caller encode the original untouched. The returned bitmap is owned by the caller.
    /// </summary>
    private static SKBitmap? ResizeToFit(SKBitmap original, int size)
    {
        if (original.Width <= size && original.Height <= size)
            return null;

        var scale = Math.Min((double)size / original.Width, (double)size / original.Height);
        var w = Math.Max(1, (int)Math.Round(original.Width * scale));
        var h = Math.Max(1, (int)Math.Round(original.Height * scale));

        var info = new SKImageInfo(w, h, original.ColorType, original.AlphaType);
        return original.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private static ResolvedCover Webp(string path) => new() { FilePath = path, ContentType = "image/webp" };

    private static long SafeLastWriteTicks(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return 0; }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
