using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Artwork;

public interface IArtistImageService
{
    /// <summary>
    /// The artist's portrait CDN URL, from the database cache or a live Deezer → Spotify lookup
    /// (each hit verified by name before it's trusted). Null when disabled, the name is blank, or
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
    IOptions<MusicEnricherOptions> options,
    ILogger<ArtistImageService> logger) : IArtistImageService
{
    public async Task<string?> GetImageUrlAsync(string name, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!opts.EnableArtistImages || string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = TitleNormalizer.NormalizeForDedup(name);
        if (normalized.Length == 0)
            return null;

        var now = DateTime.UtcNow;
        var cached = await db.ArtistImages.FirstOrDefaultAsync(a => a.NormalizedName == normalized, ct);
        if (cached is not null && !IsStale(cached, opts, now))
            return cached.ImageUrl;

        var (url, source) = await FetchAsync(name, ct);

        if (cached is null)
        {
            cached = new ArtistImage { NormalizedName = normalized, DisplayName = name.Trim() };
            db.ArtistImages.Add(cached);
        }
        else if (url is null && cached.ImageUrl is not null)
        {
            // A refresh that finds nothing keeps the old URL (CDN links usually still resolve) but
            // re-stamps the row so we don't hammer providers on every request.
            cached.FetchedAtUtc = now;
            await SaveBestEffortAsync(ct);
            return cached.ImageUrl;
        }

        cached.ImageUrl = url;
        cached.Source = source;
        cached.FetchedAtUtc = now;
        await SaveBestEffortAsync(ct);
        return url;
    }

    private static bool IsStale(ArtistImage cached, MusicEnricherOptions opts, DateTime now)
    {
        var retryDays = cached.ImageUrl is null ? opts.ArtistImageNotFoundRetryDays : opts.ArtistImageRefreshDays;
        return retryDays > 0 && cached.FetchedAtUtc <= now.AddDays(-retryDays);
    }

    private async Task<(string? Url, string? Source)> FetchAsync(string name, CancellationToken ct)
    {
        try
        {
            var deezerCandidates = await deezer.SearchArtistCandidatesAsync(name, ct);
            var deezerHit = deezerCandidates.FirstOrDefault(c =>
                ArtworkMatchVerifier.IsArtistMatch(name, c.Name) && !string.IsNullOrWhiteSpace(c.PictureUrl));
            if (deezerHit is not null)
                return (deezerHit.PictureUrl, "deezer");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deezer artist image lookup failed for {Artist}", name);
        }

        try
        {
            var (clientId, clientSecret) = await spotifyCredentials.ResolveAsync(ct);
            if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
            {
                var spotifyCandidates = await spotifyCatalog.SearchArtistCandidatesAsync(clientId, clientSecret, name, ct);
                var spotifyHit = spotifyCandidates.FirstOrDefault(c =>
                    ArtworkMatchVerifier.IsArtistMatch(name, c.Name) && !string.IsNullOrWhiteSpace(c.ImageUrl));
                if (spotifyHit is not null)
                    return (spotifyHit.ImageUrl, "spotify");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Spotify artist image lookup failed for {Artist}", name);
        }

        return (null, null);
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
