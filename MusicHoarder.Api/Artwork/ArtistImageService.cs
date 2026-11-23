using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Artwork;

public interface IArtistImageService
{
    /// <summary>
    /// The artist's portrait CDN URL, from the database cache or a live Spotify → Deezer lookup
    /// (each hit verified by name before it's trusted). Null when disabled, the name is blank or over-long, the name matches no artist in the caller's library or follows (cached value only), or
    /// no provider has a verified portrait — that outcome is negative-cached and retried after
    /// <c>MusicEnricher:ArtistImageNotFoundRetryDays</c>. Never throws (except on cancellation).
    /// </summary>
    Task<string?> GetImageUrlAsync(string name, CancellationToken ct = default);
}

public sealed class ArtistImageService(
    MusicHoarderDbContext db,
    IDeezerCatalogService deezer,
    ISpotifyCatalogSearchService spotifyCatalog,
    ISpotifyAppCredentialsProvider spotifyCredentials,
    ILibraryScopeResolver libraryScope,
    IOptions<MusicEnricherOptions> options,
    ILogger<ArtistImageService> logger) : IArtistImageService
{
    /// <summary>Longest artist name that is looked up; longer input is rejected without a lookup.</summary>
    internal const int MaxNameLength = 200;

    private readonly record struct FetchResult(string? Url, string? Source, bool SpotifyRateLimited = false);

    public async Task<string?> GetImageUrlAsync(string name, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.EnableArtistImages || string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = TitleNormalizer.NormalizeForDedup(name);
        if (normalized.Length == 0)
            return null;

        if (name.Length > MaxNameLength)
            return null;

        var now = DateTime.UtcNow;
        var cached = await db.ArtistImages.FirstOrDefaultAsync(a => a.NormalizedName == normalized, ct);
        if (cached is not null && !IsStale(cached, opts, now))
            return cached.ImageUrl;

        // Only names of artists in a library the caller can read (or followed by them) are looked
        // up and stored; any other name gets what is already cached (possibly stale) and no
        // provider call or new row.
        if (!await IsKnownArtistAsync(name, ct))
            return cached?.ImageUrl;

        var result = await FetchAsync(name, hasFallbackUrl: cached?.ImageUrl is not null, ct);

        if (result.SpotifyRateLimited)
        {
            if (cached?.ImageUrl is not null)
            {
                // Spotify couldn't answer yet — serve the existing cached portrait without
                // re-stamping FetchedAtUtc so this artist still upgrades once the rate limit clears.
                return cached.ImageUrl;
            }

            if (result.Url is null)
                return null;

            if (cached is null)
            {
                cached = new ArtistImage { NormalizedName = normalized, DisplayName = name.Trim() };
                db.ArtistImages.Add(cached);
            }

            // Serve the Deezer fallback now, but leave the row invalidated (UnixEpoch) so it
            // upgrades to Spotify once the rate limit clears.
            cached.ImageUrl = result.Url;
            cached.Source = result.Source;
            cached.FetchedAtUtc = DateTime.UnixEpoch;
            await SaveBestEffortAsync(ct);
            return result.Url;
        }

        if (cached is null)
        {
            cached = new ArtistImage { NormalizedName = normalized, DisplayName = name.Trim() };
            db.ArtistImages.Add(cached);
        }
        else if (result.Url is null && cached.ImageUrl is not null)
        {
            // A refresh that finds nothing keeps the old URL (CDN links usually still resolve) but
            // re-stamps the row so we don't hammer providers on every request.
            cached.FetchedAtUtc = now;
            await SaveBestEffortAsync(ct);
            return cached.ImageUrl;
        }

        cached.ImageUrl = result.Url;
        cached.Source = result.Source;
        cached.FetchedAtUtc = now;
        await SaveBestEffortAsync(ct);
        return result.Url;
    }

    private async Task<bool> IsKnownArtistAsync(string name, CancellationToken ct)
    {
        var lowered = name.Trim().ToLower();
        var needle = ";" + lowered + ";";
        // The caller's own songs, then each library shared with them: a member owns no songs.
        var scope = await libraryScope.ResolveAsync(db, ct);
        foreach (var slice in scope.Slices)
        {
            if (await scope.SongsFor(db, slice).AnyAsync(s =>
                    (s.Artist != null && s.Artist.ToLower() == lowered)
                    || (s.AlbumArtist != null && s.AlbumArtist.ToLower() == lowered)
                    || (s.Artists != null && (";" + s.Artists + ";").Replace("; ", ";").ToLower().Contains(needle)), ct))
                return true;
        }

        // Followed artists also render portraits (even before the caller owns any of their tracks).
        var searchKey = TitleNormalizer.NormalizeForSearch(name);
        return await db.FollowedArtists.AnyAsync(
            f => (searchKey.Length > 0 && f.ArtistKey == searchKey) || f.Name.ToLower() == lowered, ct);
    }

    private static bool IsStale(ArtistImage cached, MusicEnricherOptions opts, DateTime now)
    {
        // Rows explicitly invalidated (e.g. provider priority migration or Spotify rate-limit
        // fallback) always refresh once, even when periodic refresh is disabled (0).
        if (cached.FetchedAtUtc <= DateTime.UnixEpoch)
            return true;

        var retryDays = cached.ImageUrl is null ? opts.ArtistImageNotFoundRetryDays : opts.ArtistImageRefreshDays;
        return retryDays > 0 && cached.FetchedAtUtc <= now.AddDays(-retryDays);
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
                    return new FetchResult(spotifyHit.ImageUrl, "spotify");
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
                return new FetchResult(null, null, SpotifyRateLimited: true);
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
                return new FetchResult(deezerHit.PictureUrl, "deezer", spotifyRateLimited);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deezer artist image lookup failed for {Artist}", name);
        }

        return new FetchResult(null, null, spotifyRateLimited);
    }

    // Concurrent first-time requests for the same artist race on the unique NormalizedName index;
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
            logger.LogDebug(ex, "Artist image cache write lost a concurrency race; serving the fetched URL anyway");
            return false;
        }
    }
}
