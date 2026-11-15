using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.AppleMusic;

/// <summary>
/// An album's whole iTunes tracklist, fetched once and shared by every track of that album.
/// <para>
/// iTunes allows about 20 requests a minute, so the catalog service spaces them 4 seconds apart, and
/// the Apple Music enrichment provider used to search once or twice per track: an album of 23 tracks
/// spent one and a half to three minutes in enrichment waiting on iTunes alone, while every other
/// provider had long answered. Two requests — the album search and its tracklist lookup — give the
/// provider every track of the album to score against locally, so the rest of the album costs
/// nothing.
/// </para>
/// <para>
/// A search always answers with something, so an album counts only when its title resembles the
/// album asked for and its artist the artist asked for (<see cref="CanonicalAlbumMatch"/>, the test a
/// canonical album has to pass). An album iTunes does not carry — an unreleased leak — comes back
/// empty, and is remembered as empty, so its tracks each cost the one search they did before plus a
/// single album search for the whole album.
/// </para>
/// <para>
/// The result is cached per artist and album as written in the tags, folded the way search folds
/// text, for <see cref="MusicEnricherOptions.AppleMusicApiSearchCacheMinutes"/>. Concurrent callers
/// for the same album wait on one fetch rather than each starting their own.
/// </para>
/// </summary>
public sealed class AppleMusicAlbumIndex(
    IAppleMusicCatalogService catalog,
    IMemoryCache cache,
    IOptions<MusicEnricherOptions> options,
    ILogger<AppleMusicAlbumIndex> logger)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<AppleMusicCatalogTrack>>>> _inFlight = new();

    /// <summary>
    /// The tracks of the iTunes album that is <paramref name="album"/> by <paramref name="artist"/>,
    /// or none when iTunes has no such album. Throws <see cref="Enrichment.ProviderRateLimitedException"/>
    /// while iTunes is throttling; nothing is remembered then.
    /// </summary>
    public async Task<IReadOnlyList<AppleMusicCatalogTrack>> GetTracksAsync(
        string? artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return [];

        var key = CacheKey(artist, album);
        if (cache.TryGetValue(key, out IReadOnlyList<AppleMusicCatalogTrack>? cached) && cached is not null)
            return cached;

        // The fetch runs uncancelled so one caller giving up does not fail the others waiting on it;
        // each caller stops waiting on its own token. Its requests are bounded by the HTTP timeout.
        var load = _inFlight.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<AppleMusicCatalogTrack>>>(
            () => LoadAsync(key, artist, album)));
        try
        {
            return await load.Value.WaitAsync(ct);
        }
        finally
        {
            if (load.Value.IsCompleted)
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<AppleMusicCatalogTrack>>>>(key, load));
        }
    }

    private async Task<IReadOnlyList<AppleMusicCatalogTrack>> LoadAsync(string key, string artist, string album)
    {
        var tracks = await FetchAsync(artist, album);
        cache.Set(key, tracks, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, options.Value.AppleMusicApiSearchCacheMinutes)),
        });
        return tracks;
    }

    private async Task<IReadOnlyList<AppleMusicCatalogTrack>> FetchAsync(string artist, string album)
    {
        var opts = options.Value;
        var candidates = await catalog.SearchAlbumCandidatesAsync(artist, album, CancellationToken.None);
        var wanted = TitleNormalizer.NormalizeForSearch(album);
        var match = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.CollectionName) && !string.IsNullOrWhiteSpace(c.ArtistName))
            .Where(c => CanonicalAlbumMatch.TitleMatches(album, c.CollectionName, opts.IdentityTitleThreshold)
                        && CanonicalAlbumMatch.SameArtist(artist, c.ArtistName, opts.IdentityArtistThreshold))
            // The exact title over an edition that only contains it ("Graduation" over "Graduation (Deluxe)").
            .OrderByDescending(c => TitleNormalizer.NormalizeForSearch(c.CollectionName) == wanted)
            .FirstOrDefault();
        if (match is null)
        {
            logger.LogInformation(
                "Apple Music has no album '{Album}' by {Artist} ({Candidates} search result(s), none of them it); its tracks are searched one by one",
                album, artist, candidates.Count);
            return [];
        }

        var detail = await catalog.GetAlbumAsync(match.CollectionId, CancellationToken.None);
        if (detail is null || detail.Tracks.Count == 0)
            return [];

        var tracks = detail.Tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Title))
            .Select(t => new AppleMusicCatalogTrack(
                t.Id!,
                t.Title!,
                t.Artist ?? detail.Artist ?? match.ArtistName ?? "",
                detail.Name ?? match.CollectionName ?? "",
                detail.Year,
                t.TrackNumber > 0 ? t.TrackNumber : null,
                t.DurationMs,
                null))
            .ToList();
        logger.LogInformation(
            "Apple Music album '{AppleAlbum}' by {AppleArtist} (collection {CollectionId}) holds {Count} track(s) for '{Album}' by {Artist}",
            detail.Name, detail.Artist, detail.Id, tracks.Count, album, artist);
        return tracks;
    }

    private string CacheKey(string artist, string album)
    {
        var country = string.IsNullOrWhiteSpace(options.Value.AppleMusicCountry) ? "US" : options.Value.AppleMusicCountry.Trim();
        return $"applemusic_album:{country}|{Fold(artist)}|{Fold(album)}";
    }

    // A symbol-only name ("¥$") folds to nothing; keep it as written so two such names stay apart.
    private static string Fold(string value)
    {
        var folded = TitleNormalizer.NormalizeForSearch(value);
        return folded.Length > 0 ? folded : value.Trim().ToLowerInvariant();
    }
}
