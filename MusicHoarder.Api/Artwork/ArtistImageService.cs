using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Artwork;

public interface IArtistImageService
{
    /// <summary>
    /// The artist's portrait CDN URL, from the database cache only. Never blocks on a provider:
    /// a missing or stale row is enqueued for <see cref="ArtistImagePrefetchBackgroundService"/>
    /// and whatever is cached (possibly stale, possibly null = negative cache) is returned now.
    /// <paramref name="large"/> picks the hero-sized portrait when the provider offered one.
    /// Null when disabled, the name is blank or over-long, or no row exists yet.
    /// </summary>
    Task<string?> GetImageUrlAsync(string name, bool large = false, CancellationToken ct = default);

    /// <summary>
    /// <see cref="GetImageUrlAsync"/>, also saying whether a background lookup is pending for the
    /// name — so a miss can be cached briefly while the portrait may still arrive, and for longer
    /// once the providers have said they have none.
    /// </summary>
    Task<ArtistImageLookup> LookupAsync(string name, bool large = false, CancellationToken ct = default);

    /// <summary>
    /// Cached rows for many display names in one indexed query — the bulk map the artists grid
    /// fetches instead of one redirect per tile. Values are null for names with no row; known
    /// names without a fresh row are enqueued for background refresh.
    /// </summary>
    Task<IReadOnlyDictionary<string, ArtistImage?>> GetImagesMapAsync(IReadOnlyList<string> names, CancellationToken ct = default);

    /// <summary>
    /// One background refresh pass for a known artist: a live Spotify → Deezer lookup (each hit
    /// verified by name before it's trusted) and a cache-row upsert. Only ever called off the HTTP
    /// request path, by the prefetch worker. Never throws (except on cancellation).
    /// </summary>
    Task RefreshAsync(string name, CancellationToken ct = default);
}

/// <summary>A cached portrait URL (null when none), and whether a background lookup was just queued for it.</summary>
public readonly record struct ArtistImageLookup(string? Url, bool Pending);

public sealed class ArtistImageService(
    MusicHoarderDbContext db,
    IDeezerCatalogService deezer,
    ISpotifyCatalogSearchService spotifyCatalog,
    ISpotifyAppCredentialsProvider spotifyCredentials,
    IKnownArtistIndex knownArtists,
    IArtistImagePrefetcher prefetcher,
    IOptions<MusicEnricherOptions> options,
    ILogger<ArtistImageService> logger) : IArtistImageService
{
    /// <summary>Longest artist name that is looked up; longer input is rejected without a lookup.</summary>
    internal const int MaxNameLength = 200;

    private readonly record struct FetchResult(string? Url, string? UrlLarge, string? Source, bool SpotifyRateLimited = false);

    public async Task<string?> GetImageUrlAsync(string name, bool large = false, CancellationToken ct = default) =>
        (await LookupAsync(name, large, ct)).Url;

    public async Task<ArtistImageLookup> LookupAsync(string name, bool large = false, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.EnableArtistImages || string.IsNullOrWhiteSpace(name))
            return default;

        var normalized = TitleNormalizer.NormalizeForDedup(name);
        if (normalized.Length == 0 || name.Length > MaxNameLength)
            return default;

        var cached = await db.ArtistImages.AsNoTracking()
            .FirstOrDefaultAsync(a => a.NormalizedName == normalized, ct);

        if (cached is null)
        {
            // No row yet: only names of artists actually in a library (or followed) may trigger a
            // provider lookup — anything else gets a plain 404. The lookup itself happens in the
            // background, so this request stays fast no matter what the providers are doing.
            if (!knownArtists.IsKnown(normalized))
                return default;
            prefetcher.Enqueue(name);
            return new ArtistImageLookup(null, Pending: true);
        }

        var stale = IsStale(cached, opts, DateTime.UtcNow);
        if (stale)
            prefetcher.Enqueue(cached.DisplayName);

        // Stale-while-revalidate: serve the cached portrait immediately; the enqueued refresh
        // upgrades the row in the background.
        return new ArtistImageLookup(
            large ? cached.ImageUrlLarge ?? cached.ImageUrl : cached.ImageUrl,
            Pending: stale);
    }

    public async Task<IReadOnlyDictionary<string, ArtistImage?>> GetImagesMapAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ArtistImage?>(names.Count, StringComparer.Ordinal);
        if (!options.Value.EnableArtistImages || names.Count == 0)
        {
            foreach (var name in names)
                result[name] = null;
            return result;
        }

        var opts = options.Value;
        var now = DateTime.UtcNow;

        var byNormalized = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            {
                result[name] = null;
                continue;
            }
            var normalized = TitleNormalizer.NormalizeForDedup(name);
            if (normalized.Length == 0)
            {
                result[name] = null;
                continue;
            }
            if (!byNormalized.TryGetValue(normalized, out var list))
                byNormalized[normalized] = list = [];
            list.Add(name);
        }

        var keys = byNormalized.Keys.ToList();
        if (keys.Count > 0)
        {
            var rows = await db.ArtistImages.AsNoTracking()
                .Where(a => keys.Contains(a.NormalizedName))
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                foreach (var name in byNormalized[row.NormalizedName])
                    result[name] = row;
                if (IsStale(row, opts, now))
                    prefetcher.Enqueue(row.DisplayName);
            }
        }

        // Names with no row: enqueue the known ones (the rest are 404s by design), report null.
        foreach (var pair in byNormalized)
        {
            if (result.TryGetValue(pair.Value[0], out var row) && row is not null)
                continue;
            if (knownArtists.IsKnown(pair.Key))
            {
                var display = knownArtists.DisplayNameFor(pair.Key) ?? pair.Value[0];
                prefetcher.Enqueue(display);
            }
            foreach (var name in pair.Value)
                result[name] = null;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string name, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.EnableArtistImages || string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            return;

        var normalized = TitleNormalizer.NormalizeForDedup(name);
        if (normalized.Length == 0)
            return;

        // The prefetch worker owns the authoritative gate; a name that is not a library/followed
        // artist never reaches a provider.
        if (!knownArtists.IsKnown(normalized))
            return;

        var now = DateTime.UtcNow;
        var cached = await db.ArtistImages.FirstOrDefaultAsync(a => a.NormalizedName == normalized, ct);
        if (cached is not null && !IsStale(cached, opts, now))
            return;

        var result = await FetchAsync(name, hasFallbackUrl: cached?.ImageUrl is not null, ct);

        if (result.SpotifyRateLimited)
        {
            if (cached?.ImageUrl is not null)
            {
                // Spotify couldn't answer yet — keep the existing cached portrait without
                // re-stamping FetchedAtUtc so this artist still upgrades once the rate limit clears.
                return;
            }

            if (result.Url is null)
                return;

            if (cached is null)
            {
                cached = new ArtistImage { NormalizedName = normalized, DisplayName = name.Trim() };
                db.ArtistImages.Add(cached);
            }

            // Serve the Deezer fallback now, but leave the row invalidated (UnixEpoch) so it
            // upgrades to Spotify once the rate limit clears.
            cached.ImageUrl = result.Url;
            cached.ImageUrlLarge = result.UrlLarge;
            cached.Source = result.Source;
            cached.FetchedAtUtc = DateTime.UnixEpoch;
            await SaveBestEffortAsync(ct);
            return;
        }

        if (cached is null)
        {
            cached = new ArtistImage { NormalizedName = normalized, DisplayName = name.Trim() };
            db.ArtistImages.Add(cached);
        }
        else if (result.Url is null && cached.ImageUrl is not null)
        {
            // A refresh that finds nothing keeps the old URL (CDN links usually still resolve) but
            // re-stamps the row so we don't hammer providers on every sweep.
            cached.FetchedAtUtc = now;
            await SaveBestEffortAsync(ct);
            return;
        }

        cached.ImageUrl = result.Url;
        cached.ImageUrlLarge = result.UrlLarge;
        cached.Source = result.Source;
        cached.FetchedAtUtc = now;
        await SaveBestEffortAsync(ct);
    }

    internal static bool IsStale(ArtistImage cached, MusicEnricherOptions opts, DateTime now) =>
        IsStale(cached.FetchedAtUtc, cached.ImageUrl is not null, opts, now);

    internal static bool IsStale(DateTime fetchedAtUtc, bool hasImage, MusicEnricherOptions opts, DateTime now)
    {
        // Rows explicitly invalidated (e.g. provider priority migration or Spotify rate-limit
        // fallback) always refresh once, even when periodic refresh is disabled (0).
        if (fetchedAtUtc <= DateTime.UnixEpoch)
            return true;

        var retryDays = hasImage ? opts.ArtistImageRefreshDays : opts.ArtistImageNotFoundRetryDays;
        return retryDays > 0 && fetchedAtUtc <= now.AddDays(-retryDays);
    }

    private async Task<FetchResult> FetchAsync(string name, bool hasFallbackUrl, CancellationToken ct)
    {
        var spotifyRateLimited = false;
        try
        {
            var (clientId, clientSecret) = await spotifyCredentials.ResolveAsync(ct);
            if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
            {
                var spotifyCandidates = await spotifyCatalog.SearchArtistCandidatesAsync(clientId, clientSecret, name, ct);
                var spotifyHit = spotifyCandidates.FirstOrDefault(c =>
                    ArtworkMatchVerifier.IsArtistMatch(name, c.Name) && !string.IsNullOrWhiteSpace(c.ImageUrl));
                if (spotifyHit is not null)
                    return new FetchResult(spotifyHit.ImageUrl, spotifyHit.ImageUrlLarge ?? spotifyHit.ImageUrl, "spotify");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderRateLimitedException ex)
        {
            spotifyRateLimited = true;
            logger.LogWarning(ex, "Spotify artist image lookup rate limited for {Artist}", name);
            if (hasFallbackUrl)
                return new FetchResult(null, null, null, SpotifyRateLimited: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Spotify artist image lookup failed for {Artist}", name);
        }

        try
        {
            var deezerCandidates = await deezer.SearchArtistCandidatesAsync(name, ct);
            var deezerHit = deezerCandidates.FirstOrDefault(c =>
                ArtworkMatchVerifier.IsArtistMatch(name, c.Name) && !string.IsNullOrWhiteSpace(c.PictureUrl));
            if (deezerHit is not null)
                return new FetchResult(deezerHit.PictureUrl, deezerHit.PictureUrlLarge ?? deezerHit.PictureUrl, "deezer", spotifyRateLimited);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deezer artist image lookup failed for {Artist}", name);
        }

        return new FetchResult(null, null, null, spotifyRateLimited);
    }

    // Concurrent first-time fetches for the same artist race on the unique NormalizedName index;
    // the loser's insert fails, which is fine — the winner's row is already there for the retry.
    private async Task<bool> SaveBestEffortAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogDebug(ex, "Artist image cache write lost a concurrency race; the winner's row stands");
            return false;
        }
    }
}
