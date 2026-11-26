using System.Collections.Concurrent;
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
    /// The cache file a thumbnail for <paramref name="identityPath"/> at <paramref name="size"/>
    /// would live in, when it already exists — without touching the source image. Null when the
    /// thumbnail isn't cached yet (the caller then goes through
    /// <see cref="GetThumbnailAsync"/>, which generates it).
    /// </summary>
    string? GetCachedPath(string identityPath, long identityStamp, int requestedSize);

    /// <summary>
    /// Returns a cached WebP thumbnail of <paramref name="source"/> at <paramref name="size"/> (max
    /// edge, no upscaling), generating + caching it on first request. Concurrent misses for the
    /// same source/size share one generation (single-flight). <paramref name="identityPath"/> is
    /// the on-disk file the cover derives from (the folder image, or the audio file for embedded
    /// art) — its last-write time keys the cache so an edited cover regenerates. Null if the source
    /// can't be decoded.
    /// </summary>
    Task<ResolvedCover?> GetThumbnailAsync(ResolvedCover source, string identityPath, int size, CancellationToken ct = default);

    /// <summary>
    /// Generates the grid sizes (128/400) for a cover in the background when nothing is asking for
    /// them yet — called when a cover is written or a song is built, so scrolling never pays the
    /// first-generation cost. Fire-and-forget and not cancellable: it outlives whatever asked for
    /// it. Bounded concurrency, shares in-flight generations with requests, and drops (never
    /// queues) passes beyond a small pending cap.
    /// </summary>
    void Prewarm(ResolvedCover source, string identityPath);
}

/// <summary>
/// Resizes album covers to small WebP variants and caches them on disk. The cache is a disposable,
/// content-addressed directory of derived artifacts — wiping it just makes them regenerate; the
/// originals (audio files / destination <c>cover.jpg</c>) are never touched.
/// </summary>
public sealed class CoverThumbnailService(string cacheDirectory, ILogger<CoverThumbnailService> logger) : ICoverThumbnailService
{
    private static readonly int[] BucketSizes = [128, 256, 400, 640];

    /// <summary>The sizes grids actually ask for; prewarmed when a cover lands.</summary>
    internal static readonly int[] PrewarmSizes = [128, 400];

    // Single-flight: concurrent misses for the same identity+size share one generation instead of
    // N requests each decoding the same multi-MB original. A generation never runs on a request's
    // token (one caller giving up must not fail the others); each caller only stops *waiting* when
    // its own request goes away. The entry leaves the map once its pass is over — the cache file's
    // existence is the source of truth — and only that entry, never a newer one under the same key.
    private readonly ConcurrentDictionary<string, Task<ResolvedCover?>> _inFlight = new(StringComparer.Ordinal);

    // Prewarm passes, deduped so a build storm doesn't queue one pass per song, and capped: past
    // MaxPendingPrewarms a pass is dropped rather than queued (each one pins its source bytes), and
    // the next request for that cover generates it on demand instead.
    internal const int MaxPendingPrewarms = 64;
    private readonly ConcurrentDictionary<string, byte> _prewarming = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _prewarmSlots = new(2);

    public string CacheDirectory => cacheDirectory;

    public int ClampToBucket(int requestedSize)
    {
        foreach (var b in BucketSizes)
            if (requestedSize <= b)
                return b;
        return BucketSizes[^1];
    }

    public string? GetCachedPath(string identityPath, long identityStamp, int requestedSize)
    {
        var cacheFile = CacheFileFor(identityPath, identityStamp, requestedSize);
        return File.Exists(cacheFile) ? cacheFile : null;
    }

    public Task<ResolvedCover?> GetThumbnailAsync(ResolvedCover source, string identityPath, int size, CancellationToken ct = default)
    {
        var stamp = SafeLastWriteTicks(identityPath);
        var cacheFile = CacheFileFor(identityPath, stamp, size);

        if (File.Exists(cacheFile))
            return Task.FromResult<ResolvedCover?>(Webp(cacheFile));

        return GenerateOnceAsync(source, identityPath, stamp, size, cacheFile).WaitAsync(ct);
    }

    /// <summary>The one generation of this identity+size, started now unless one is already running.</summary>
    private Task<ResolvedCover?> GenerateOnceAsync(
        ResolvedCover source, string identityPath, long stamp, int size, string cacheFile)
    {
        var key = ThumbnailKey(identityPath, stamp, size);
        while (true)
        {
            if (_inFlight.TryGetValue(key, out var running))
                return running;

            var done = new TaskCompletionSource<ResolvedCover?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlight.TryAdd(key, done.Task))
                continue;

            _ = RunAsync();
            return done.Task;

            async Task RunAsync()
            {
                try
                {
                    done.SetResult(await GenerateAsync(source, identityPath, stamp, size, cacheFile).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
                finally
                {
                    _inFlight.TryRemove(new KeyValuePair<string, Task<ResolvedCover?>>(key, done.Task));
                }
            }
        }
    }

    public void Prewarm(ResolvedCover source, string identityPath)
    {
        var stamp = SafeLastWriteTicks(identityPath);
        var key = $"{ThumbnailKey(identityPath, stamp, 0)}|prewarm";
        if (_prewarming.Count >= MaxPendingPrewarms || !_prewarming.TryAdd(key, 0))
            return;

        // No token: a prewarm outlives the request or pipeline step that asked for it, and a token
        // that fired before the delegate started would skip the finally and pin the dedupe key.
        _ = Task.Run(async () =>
        {
            try
            {
                await _prewarmSlots.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var size in PrewarmSizes)
                    {
                        var cacheFile = CacheFileFor(identityPath, stamp, size);
                        if (File.Exists(cacheFile))
                            continue;
                        await GenerateOnceAsync(source, identityPath, stamp, size, cacheFile).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _prewarmSlots.Release();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Cover prewarm failed for {Path}", identityPath);
            }
            finally
            {
                _prewarming.TryRemove(key, out _);
            }
        });
    }

    private async Task<ResolvedCover?> GenerateAsync(
        ResolvedCover source, string identityPath, long stamp, int size, string cacheFile)
    {
        try
        {
            var bytes = source.FilePath is not null
                ? await File.ReadAllBytesAsync(source.FilePath)
                : source.Bytes!;

            using var original = SKBitmap.Decode(bytes);
            if (original is null)
                return null; // undecodable image

            // Fit within the box, preserving aspect; never enlarge a smaller original.
            using var resized = ResizeToFit(original, ClampToBucket(size));

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var tmp = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
            using (var image = SKImage.FromBitmap(resized ?? original))
            using (var data = image.Encode(SKEncodedImageFormat.Webp, 80))
            await using (var fs = File.Create(tmp))
                data.SaveTo(fs);
            File.Move(tmp, cacheFile, overwrite: true);

            return Webp(cacheFile);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cover thumbnail generation failed for {Path} @ {Size}", identityPath, size);
            return null;
        }
    }

    private string CacheFileFor(string identityPath, long stamp, int requestedSize)
    {
        var hash = Hash(ThumbnailKey(identityPath, stamp, requestedSize));
        return Path.Combine(cacheDirectory, hash[..2], $"{hash}.webp");
    }

    private static string ThumbnailKey(string identityPath, long stamp, int requestedSize) =>
        $"{identityPath}|{stamp}|{ClampToBucketStatic(requestedSize)}";

    private static int ClampToBucketStatic(int requestedSize)
    {
        foreach (var b in BucketSizes)
            if (requestedSize <= b)
                return b;
        return BucketSizes[^1];
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
