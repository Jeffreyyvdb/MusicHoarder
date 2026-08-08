using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.CoverArtArchive;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// The identifiers an album folder offers for external cover lookup. <see cref="SpotifyTrackId"/> is
/// a representative track's matched Spotify id — the album image it belongs to is official art, so
/// it outranks any text search.
/// </summary>
public sealed record ExternalCoverArtQuery(
    string? MusicBrainzReleaseId,
    string? MusicBrainzReleaseGroupId,
    string? AlbumArtist,
    string? Album,
    string? SpotifyTrackId = null,
    string? SpotifyAlbumId = null);

/// <summary>A validated cover image. <see cref="Source"/> ∈ coverartarchive | spotify | deezer | itunes.</summary>
public sealed record FetchedCoverArt(byte[] Bytes, string ContentType, string Source);

/// <summary>
/// <see cref="HadTransientFailure"/> distinguishes "no provider has this cover" (a long NotFound
/// cooldown is fine) from "a provider errored or was rate limited" (retry sooner).
/// </summary>
public sealed record ExternalCoverArtFetchResult(FetchedCoverArt? Cover, bool HadTransientFailure);

public interface IExternalCoverArtFetcher
{
    /// <summary>
    /// Tries the provider chain (Cover Art Archive by MBID → Spotify → Deezer → iTunes) and returns
    /// the first usable front cover. Never throws (except on cancellation); provider errors fall
    /// through to the next provider and surface as <see cref="ExternalCoverArtFetchResult.HadTransientFailure"/>.
    /// </summary>
    Task<ExternalCoverArtFetchResult> FetchAsync(ExternalCoverArtQuery query, CancellationToken ct = default);
}

public sealed class ExternalCoverArtFetcher(
    ICoverArtArchiveClient coverArtArchive,
    ISpotifyCatalogSearchService spotifyCatalog,
    ISpotifyAppCredentialsProvider spotifyCredentials,
    IDeezerCatalogService deezer,
    IAppleMusicCatalogService appleMusic,
    HttpClient imageHttpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<ExternalCoverArtFetcher> logger) : IExternalCoverArtFetcher
{
    public async Task<ExternalCoverArtFetchResult> FetchAsync(ExternalCoverArtQuery query, CancellationToken ct = default)
    {
        var opts = options.Value;
        var hadTransientFailure = false;

        if (opts.EnableCoverArtArchiveCovers)
        {
            var (cover, failed) = await TryProviderAsync("coverartarchive", () => FetchFromCoverArtArchiveAsync(query, ct));
            hadTransientFailure |= failed;
            if (cover is not null)
                return new ExternalCoverArtFetchResult(cover, hadTransientFailure);
        }

        if (opts.EnableSpotifyCovers
            && (!string.IsNullOrWhiteSpace(query.SpotifyAlbumId)
                || !string.IsNullOrWhiteSpace(query.SpotifyTrackId)
                || !string.IsNullOrWhiteSpace(query.Album)))
        {
            var (cover, failed) = await TryProviderAsync("spotify", () => FetchFromSpotifyAsync(query, ct));
            hadTransientFailure |= failed;
            if (cover is not null)
                return new ExternalCoverArtFetchResult(cover, hadTransientFailure);
        }

        if (opts.EnableDeezerCovers && !string.IsNullOrWhiteSpace(query.Album))
        {
            var (cover, failed) = await TryProviderAsync("deezer", () => FetchFromDeezerAsync(query, ct));
            hadTransientFailure |= failed;
            if (cover is not null)
                return new ExternalCoverArtFetchResult(cover, hadTransientFailure);
        }

        if (opts.EnableAppleMusicCovers && !string.IsNullOrWhiteSpace(query.Album))
        {
            var (cover, failed) = await TryProviderAsync("itunes", () => FetchFromItunesAsync(query, ct));
            hadTransientFailure |= failed;
            if (cover is not null)
                return new ExternalCoverArtFetchResult(cover, hadTransientFailure);
        }

        return new ExternalCoverArtFetchResult(null, hadTransientFailure);
    }

    private async Task<(FetchedCoverArt? Cover, bool Failed)> TryProviderAsync(
        string source, Func<Task<FetchedCoverArt?>> fetch)
    {
        try
        {
            return (await fetch(), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogDebug("Cover provider {Source} rate limited; retry after {Delay}s", source, ex.RetryAfter.TotalSeconds);
            return (null, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cover provider {Source} failed", source);
            return (null, true);
        }
    }

    private async Task<FetchedCoverArt?> FetchFromCoverArtArchiveAsync(ExternalCoverArtQuery query, CancellationToken ct)
    {
        var image = !string.IsNullOrWhiteSpace(query.MusicBrainzReleaseId)
            ? await coverArtArchive.GetReleaseFrontAsync(query.MusicBrainzReleaseId, ct)
            : null;
        if (image is null && !string.IsNullOrWhiteSpace(query.MusicBrainzReleaseGroupId))
            image = await coverArtArchive.GetReleaseGroupFrontAsync(query.MusicBrainzReleaseGroupId, ct);

        return image is null ? null : Validate(image.Bytes, image.ContentType, "coverartarchive");
    }

    private async Task<FetchedCoverArt?> FetchFromSpotifyAsync(ExternalCoverArtQuery query, CancellationToken ct)
    {
        var (clientId, clientSecret) = await spotifyCredentials.ResolveAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        // ID-first: the enrichment match's track/album id points at the official album image, no
        // search ambiguity involved. Text search is the last resort and must pass verification.
        var albumId = query.SpotifyAlbumId;
        if (string.IsNullOrWhiteSpace(albumId) && !string.IsNullOrWhiteSpace(query.SpotifyTrackId))
            albumId = await spotifyCatalog.GetTrackAlbumIdAsync(clientId, clientSecret, query.SpotifyTrackId, ct);

        if (string.IsNullOrWhiteSpace(albumId) && !string.IsNullOrWhiteSpace(query.Album))
        {
            var candidates = await spotifyCatalog.SearchAlbumCandidatesAsync(clientId, clientSecret, query.AlbumArtist ?? "", query.Album, ct);
            albumId = candidates
                .FirstOrDefault(c => ArtworkMatchVerifier.IsAlbumMatch(query.AlbumArtist, query.Album, c.Artist, c.Name))
                ?.Id;
        }

        if (string.IsNullOrWhiteSpace(albumId))
            return null;

        var album = await spotifyCatalog.GetAlbumAsync(clientId, clientSecret, albumId, ct);
        if (string.IsNullOrWhiteSpace(album?.ImageUrl))
            return null;

        return await DownloadImageAsync(album.ImageUrl, "spotify", ct);
    }

    private async Task<FetchedCoverArt?> FetchFromDeezerAsync(ExternalCoverArtQuery query, CancellationToken ct)
    {
        var candidates = await deezer.SearchAlbumCandidatesAsync(query.AlbumArtist ?? "", query.Album!, ct);
        var albumId = candidates
            .FirstOrDefault(c => ArtworkMatchVerifier.IsAlbumMatch(query.AlbumArtist, query.Album, c.ArtistName, c.Title))
            ?.Id;
        if (albumId is null)
            return null;

        var album = await deezer.GetAlbumAsync(albumId, ct);
        if (string.IsNullOrWhiteSpace(album?.CoverUrl))
            return null;

        return await DownloadImageAsync(album.CoverUrl, "deezer", ct);
    }

    private async Task<FetchedCoverArt?> FetchFromItunesAsync(ExternalCoverArtQuery query, CancellationToken ct)
    {
        var candidates = await appleMusic.SearchAlbumCandidatesAsync(query.AlbumArtist ?? "", query.Album!, ct);
        var collectionId = candidates
            .FirstOrDefault(c => ArtworkMatchVerifier.IsAlbumMatch(query.AlbumArtist, query.Album, c.ArtistName, c.CollectionName))
            ?.CollectionId;
        if (collectionId is null)
            return null;

        var album = await appleMusic.GetAlbumAsync(collectionId, ct);
        if (string.IsNullOrWhiteSpace(album?.ArtworkUrl))
            return null;

        var upgraded = UpgradeItunesArtworkUrl(album.ArtworkUrl);
        var cover = await DownloadImageAsync(upgraded, "itunes", ct);
        if (cover is null && !string.Equals(upgraded, album.ArtworkUrl, StringComparison.Ordinal))
            cover = await DownloadImageAsync(album.ArtworkUrl, "itunes", ct);
        return cover;
    }

    /// <summary>
    /// iTunes search returns 100x100 thumbnails, but the same CDN path serves the full-resolution
    /// original when the size segment is rewritten.
    /// </summary>
    internal static string UpgradeItunesArtworkUrl(string url)
        => url.Contains("100x100bb.", StringComparison.Ordinal)
            ? url.Replace("100x100bb.", "3000x3000bb.", StringComparison.Ordinal)
            : url;

    private async Task<FetchedCoverArt?> DownloadImageAsync(string url, string source, CancellationToken ct)
    {
        using var response = await imageHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Cover image download failed ({Status}) from {Source}", (int)response.StatusCode, source);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return Validate(bytes, response.Content.Headers.ContentType?.MediaType, source);
    }

    // Sniffed magic bytes win over the reported content-type (CDNs lie); the reported type is only
    // trusted when it at least claims to be an image. Tiny payloads are error pages or placeholders.
    private FetchedCoverArt? Validate(byte[] bytes, string? reportedContentType, string source)
    {
        if (bytes.Length < options.Value.ExternalCoverArtMinImageBytes)
        {
            logger.LogDebug("Rejected {Source} cover: {Bytes} bytes is below the minimum", source, bytes.Length);
            return null;
        }

        var contentType = CoverArtResolver.SniffImageMime(bytes)
            ?? (reportedContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                ? reportedContentType
                : null);
        if (contentType is null)
        {
            logger.LogDebug("Rejected {Source} cover: response is not a recognizable image", source);
            return null;
        }

        return new FetchedCoverArt(bytes, contentType, source);
    }
}
