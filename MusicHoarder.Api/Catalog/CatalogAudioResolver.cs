using System.Collections.Concurrent;
using System.Web;
using Microsoft.Extensions.Caching.Memory;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Logging;

namespace MusicHoarder.Api.Catalog;

public interface ICatalogAudioResolver
{
    /// <summary>The cached stream for a Deezer track, or a fresh resolve. Concurrent callers share one resolve.</summary>
    /// <exception cref="CatalogResolveException">Nothing usable, or yt-dlp failed or is missing.</exception>
    Task<ResolvedAudio> ResolveAsync(string deezerId, CancellationToken ct);

    /// <summary>Forgets <paramref name="staleUrl"/> if it is still the cached stream; a newer one stays.</summary>
    void Invalidate(string deezerId, string staleUrl);
}

/// <summary>Finds an audio stream on YouTube for a track.</summary>
public interface ICatalogAudioLocator
{
    /// <exception cref="CatalogResolveException"/>
    Task<ResolvedAudio> LocateAsync(DownloadRequest track, CancellationToken ct);
}

/// <summary>
/// Deezer id to a playable YouTube stream, cached per id for as long as googlevideo's signed URL
/// lives. A resolve costs a yt-dlp search and an extraction, so callers that arrive while one runs
/// wait for it instead of starting their own.
/// </summary>
public sealed class CatalogAudioResolver(
    IDeezerCatalogService deezer,
    ICatalogAudioLocator locator,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<CatalogAudioResolver> logger) : ICatalogAudioResolver
{
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(90);

    // Skimming results starts a resolve per track; each is a yt-dlp search plus an extraction
    // against YouTube, which also serves the wishlist downloads.
    private readonly SemaphoreSlim ytDlpSlots = new(2);

    private readonly ConcurrentDictionary<string, Lazy<Task<ResolvedAudio>>> inflight = new(StringComparer.Ordinal);

    public Task<ResolvedAudio> ResolveAsync(string deezerId, CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey(deezerId), out ResolvedAudio? hit) && hit is not null)
            return Task.FromResult(hit);

        // A caller that leaves does not cancel the shared run; the next caller still wants it.
        var run = inflight.GetOrAdd(deezerId, id => new Lazy<Task<ResolvedAudio>>(() => StartResolve(id))).Value;
        return run.WaitAsync(ct);
    }

    public void Invalidate(string deezerId, string staleUrl)
    {
        if (cache.TryGetValue(CacheKey(deezerId), out ResolvedAudio? hit) && hit?.StreamUrl == staleUrl)
            cache.Remove(CacheKey(deezerId));
    }

    private Task<ResolvedAudio> StartResolve(string deezerId)
    {
        var run = ResolveCoreAsync(deezerId);
        // Every waiter may have left before it fails; observe the fault so it is not reported unobserved.
        _ = run.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return run;
    }

    private async Task<ResolvedAudio> ResolveCoreAsync(string deezerId)
    {
        try
        {
            using var budget = new CancellationTokenSource(ResolveBudget);

            DeezerCatalogTrack? track;
            try
            {
                track = await deezer.LookupByIdAsync(deezerId, budget.Token);
            }
            // The Deezer client's own timeout surfaces as a cancellation that is not this run's budget.
            catch (Exception ex) when (ex is HttpRequestException or ProviderRateLimitedException or TimeoutException
                || ex is OperationCanceledException && !budget.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Deezer lookup failed for catalog track {DeezerId}", LogSanitizer.ForLog(deezerId));
                throw new CatalogResolveException(CatalogResolveFailure.CatalogUnavailable, "Deezer could not be reached. Try again in a moment.");
            }
            if (track is null)
                throw new CatalogResolveException(CatalogResolveFailure.NotFound, "Deezer has no such track.");

            var request = new DownloadRequest(track.Artist, track.Title, track.AlbumName, track.Isrc, track.DurationMs, DestinationDirectory: "");
            ResolvedAudio audio;
            await ytDlpSlots.WaitAsync(budget.Token);
            try
            {
                audio = await locator.LocateAsync(request, budget.Token);
            }
            finally
            {
                ytDlpSlots.Release();
            }

            var lifetime = AudioUrlExpiry.CacheLifetime(audio.StreamUrl, time.GetUtcNow());
            if (lifetime > TimeSpan.Zero)
                cache.Set(CacheKey(deezerId), audio, lifetime);

            logger.LogInformation("Resolved catalog track {DeezerId} to YouTube video {VideoId}", LogSanitizer.ForLog(deezerId), audio.YouTubeVideoId);
            return audio;
        }
        catch (OperationCanceledException)
        {
            throw new CatalogResolveException(CatalogResolveFailure.YtDlpFailed, "Finding the full song took too long.");
        }
        finally
        {
            inflight.TryRemove(deezerId, out _);
        }
    }

    private static string CacheKey(string deezerId) => $"catalog_full:{deezerId}";
}

internal static class AudioUrlExpiry
{
    internal static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);
    internal static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(2);
    // A signed URL without a readable expiry is still short-lived; this is a guess, not a promise.
    internal static readonly TimeSpan UnknownExpiryLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long to keep a resolved URL: until shortly before its <c>expire=</c> epoch-seconds value,
    /// at most an hour. Zero when it is about to lapse, so it is not kept at all.
    /// </summary>
    internal static TimeSpan CacheLifetime(string url, DateTimeOffset now)
    {
        if (!TryReadExpiry(url, out var expiresAt))
            return UnknownExpiryLifetime;

        var left = expiresAt - now - SafetyMargin;
        if (left <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return left < MaxLifetime ? left : MaxLifetime;
    }

    internal static bool TryReadExpiry(string url, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var raw = HttpUtility.ParseQueryString(uri.Query)["expire"];
        if (!long.TryParse(raw, out var seconds) || seconds <= 0)
            return false;
        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
