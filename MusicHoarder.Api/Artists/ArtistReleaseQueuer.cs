using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artists;

/// <summary>What queueing one release did.</summary>
/// <param name="Queued">New wishlist items created.</param>
/// <param name="Requeued">Failed items of this release put back in the queue (explicit requests only).</param>
/// <param name="AlreadyOwned">Tracks the owner already has.</param>
/// <param name="AlreadyQueued">Tracks already on the wishlist, from this release or another.</param>
/// <param name="Capped">Tracks left out because the request's track budget ran out.</param>
/// <param name="Error">Set when nothing could be read for the release.</param>
public sealed record ReleaseQueueResult(
    string? Title,
    int? CanonicalAlbumId,
    int Queued,
    int Requeued,
    int AlreadyOwned,
    int AlreadyQueued,
    int Capped,
    string? Error = null)
{
    public static ReleaseQueueResult Failed(string error) => new(null, null, 0, 0, 0, 0, 0, error);
}

/// <summary>
/// Queues one catalog release's missing tracks as wishlist items — the shared step behind "Get album",
/// "Get discography" and a followed artist's new release. Everything downstream (the downloader, the
/// scanner, enrichment, the builder) is the existing wishlist pipeline.
/// <para>
/// The release's tracklist becomes (or reuses) a <see cref="CanonicalAlbum"/> under the same (artist,
/// album) key the library groups on, so the album page shows what is still missing and album completion
/// sees the rows already queued for it and leaves them alone. The release was picked by id from a
/// trusted catalog artist, so no search is involved and <see cref="CanonicalAlbumMatch"/>'s proof is not
/// needed here: there is no "something else" a search could have answered with.
/// </para>
/// </summary>
public sealed class ArtistReleaseQueuer(
    MusicHoarderDbContext db,
    IDeezerCatalogService deezer,
    IAlbumCompletionIdentityResolver identityResolver,
    IOptions<MusicEnricherOptions> options,
    ILogger<ArtistReleaseQueuer> logger)
{
    /// <summary>Editions read per release when choosing the biggest tracklist.</summary>
    private const int MaxEditionsRead = 4;

    /// <summary>Tracks whose ISRC is looked up on Deezer when Spotify could not identify them.</summary>
    private const int MaxIsrcLookups = 40;

    /// <param name="artistName">The library's (or the follow's) name for the artist.</param>
    /// <param name="editionIds">The release's catalog ids; the edition with the most tracks is used.</param>
    /// <param name="kind">Singles and EPs skip any track the artist already has on another release.</param>
    /// <param name="trackBudget">At most this many new items; the rest are reported as capped.</param>
    /// <param name="requeueFailed">An explicit request retries this release's failed tracks; a follow does not.</param>
    public async Task<ReleaseQueueResult> QueueAsync(
        Guid ownerId,
        string artistName,
        IReadOnlyList<string> editionIds,
        ArtistReleaseKinds kind,
        WishlistItemOrigin origin,
        int trackBudget,
        bool requeueFailed,
        CancellationToken ct)
    {
        var detail = await ReadBiggestEditionAsync(editionIds, ct);
        if (detail is null || detail.Tracks.Count == 0 || string.IsNullOrWhiteSpace(detail.Title))
            return ReleaseQueueResult.Failed("The catalog did not return this release's tracklist.");

        var opts = options.Value;
        var now = DateTime.UtcNow;
        var albumArtist = string.IsNullOrWhiteSpace(detail.Artist) ? artistName : detail.Artist!;
        var canonical = await UpsertCanonicalAsync(detail, albumArtist, now, ct);

        var orderedTracks = canonical.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();

        // What the owner has: the album's own tracks (under its album key), and for a single or an EP
        // anything of the artist's by that title — a single is usually a cut of an album already owned.
        var artistSongs = await ArtistSongs.LoadAsync(
            db.Songs.IgnoreQueryFilters().Where(s => s.OwnerUserId == ownerId), artistName, ct);
        var albumKey = canonical.AlbumKey;
        var albumSongs = artistSongs
            .Where(s => TitleNormalizer.NormalizeForSearch(s.Album) == albumKey)
            .Select(s => new OwnedTrackInfo(s.Id, s.MusicBrainzId, s.DiscNumber, s.TrackNumber, s.Title))
            .ToList();
        var matched = AlbumOwnedTrackMatcher.Match(orderedTracks, albumSongs, opts.IdentityTitleThreshold);
        var ownedTitles = kind is ArtistReleaseKinds.Single or ArtistReleaseKinds.Ep
            ? artistSongs.Select(s => TitleNormalizer.NormalizeForSearch(s.Title)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal)
            : [];

        // Every item ever created for this release, any status: the per-album tombstones.
        var existing = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.CanonicalAlbumId == canonical.Id)
            .ToListAsync(ct);
        var existingKeys = existing
            .Select(w => TitleNormalizer.NormalizeForSearch(w.Title))
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var requeued = 0;
        if (requeueFailed)
        {
            foreach (var item in existing.Where(w =>
                         w.Status == WishlistItemStatus.NotFound
                         || (w.Status == WishlistItemStatus.Failed && w.NextAttemptAtUtc is null)))
            {
                item.Requeue(now);
                requeued++;
            }
        }

        // Any acquisition of the same recording already in flight, whatever asked for it — the cross-
        // album claim album completion keeps, widened to every origin.
        var claims = await LoadClaimsAsync(ownerId, ct);

        var trackArtists = detail.Tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .GroupBy(t => TitleNormalizer.NormalizeForSearch(t.Title))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var missing = new List<(CanonicalAlbumTrack Track, string Artist, string? DeezerId)>();
        int alreadyOwned = 0, alreadyQueued = 0;
        foreach (var track in orderedTracks)
        {
            if (string.IsNullOrWhiteSpace(track.Title)) continue;
            var titleKey = TitleNormalizer.NormalizeForSearch(track.Title);
            if (matched.ContainsKey(track.Id) || (titleKey.Length > 0 && ownedTitles.Contains(titleKey)))
            {
                alreadyOwned++;
                continue;
            }

            if (titleKey.Length > 0 && existingKeys.Contains(titleKey))
            {
                alreadyQueued++;
                continue;
            }

            var source = trackArtists.GetValueOrDefault(titleKey);
            var trackArtist = string.IsNullOrWhiteSpace(source?.Artist) ? albumArtist : source!.Artist!;
            var deezerId = source?.Id;
            if ((deezerId is not null && claims.DeezerIds.Contains(deezerId))
                || (ClaimKey(trackArtist, track.Title) is { } claim && claims.Tracks.Contains(claim)))
            {
                alreadyQueued++;
                continue;
            }

            missing.Add((track, trackArtist, deezerId));
        }

        var capped = 0;
        var budget = Math.Max(0, trackBudget);
        if (missing.Count > budget)
        {
            capped = missing.Count - budget;
            missing = missing.Take(budget).ToList();
        }

        if (missing.Count > 0)
        {
            var spotifyIds = await ResolveSpotifyIdsAsync(canonical, missing.Select(m => m.Track).ToList(), claims.SpotifyIds, ct);
            var isrcs = await ResolveIsrcsAsync(missing.Where(m => !spotifyIds.ContainsKey(m.Track.Id)).Select(m => m.DeezerId), ct);

            foreach (var (track, trackArtist, deezerId) in missing)
            {
                if (ClaimKey(trackArtist, track.Title) is { } claim) claims.Tracks.Add(claim);
                var usableDeezerId = deezerId is not null && claims.DeezerIds.Add(deezerId) ? deezerId : null;
                db.WishlistItems.Add(new WishlistItem
                {
                    OwnerUserId = ownerId,
                    WishlistSourceId = null,
                    Origin = origin,
                    CanonicalAlbumId = canonical.Id,
                    Title = Truncate(track.Title!, 512),
                    Artist = Truncate(trackArtist, 512),
                    Album = canonical.DisplayTitle is null ? null : Truncate(canonical.DisplayTitle, 512),
                    DurationMs = track.DurationMs ?? 0,
                    AlbumArt = canonical.CoverArtUrl is null ? null : Truncate(canonical.CoverArtUrl, 1024),
                    SpotifyTrackId = spotifyIds.GetValueOrDefault(track.Id),
                    DeezerTrackId = usableDeezerId,
                    Isrc = usableDeezerId is not null ? isrcs.GetValueOrDefault(usableDeezerId) : null,
                    Status = WishlistItemStatus.Pending,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Artist release {Artist} - {Album}: queued {Queued}, requeued {Requeued}, owned {Owned}, already queued {AlreadyQueued}, capped {Capped}",
            albumArtist, canonical.DisplayTitle, missing.Count, requeued, alreadyOwned, alreadyQueued, capped);

        return new ReleaseQueueResult(
            canonical.DisplayTitle, canonical.Id, missing.Count, requeued, alreadyOwned, alreadyQueued, capped);
    }

    private async Task<DeezerAlbumDetail?> ReadBiggestEditionAsync(IReadOnlyList<string> editionIds, CancellationToken ct)
    {
        DeezerAlbumDetail? best = null;
        foreach (var id in editionIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).Take(MaxEditionsRead))
        {
            var detail = await deezer.GetAlbumAsync(id, ct);
            if (detail is not null && (best is null || detail.Tracks.Count > best.Tracks.Count))
                best = detail;
        }

        return best;
    }

    /// <summary>
    /// The release as a canonical album under the library's (artist, album) key. A row already reconciled
    /// from several providers is kept unless this edition is bigger — then the edition asked for wins,
    /// because a deluxe request must not be cut down to the standard tracklist.
    /// </summary>
    private async Task<CanonicalAlbum> UpsertCanonicalAsync(
        DeezerAlbumDetail detail, string albumArtist, DateTime now, CancellationToken ct)
    {
        var artistKey = TitleNormalizer.NormalizeForSearch(albumArtist);
        var albumKey = TitleNormalizer.NormalizeForSearch(detail.Title);
        var row = await db.CanonicalAlbums
            .Include(a => a.Tracks)
            .FirstOrDefaultAsync(a => a.ArtistKey == artistKey && a.AlbumKey == albumKey, ct);

        if (row is { Status: CanonicalAlbumStatus.Fetched } && row.Tracks.Count >= detail.Tracks.Count)
            return row;

        if (row is null)
        {
            row = new CanonicalAlbum { ArtistKey = artistKey, AlbumKey = albumKey };
            db.CanonicalAlbums.Add(row);
        }
        else
        {
            db.CanonicalAlbumTracks.RemoveRange(row.Tracks);
            row.Tracks.Clear();
        }

        row.DisplayTitle = detail.Title;
        row.DisplayArtist = albumArtist;
        row.Year = detail.Year;
        row.CoverArtUrl = detail.CoverUrl;
        row.ResolvedTrackCount = detail.Tracks.Count;
        row.TrackCountContested = false;
        row.SourcesJson = CanonicalAlbumSources.Serialize(
            [new AlbumTracklistReconciler.ReconciledSource(EnrichmentProvider.Deezer, detail.Id, detail.Tracks.Count, true)]);
        row.Status = CanonicalAlbumStatus.Fetched;
        row.FetchedAtUtc = now;
        row.NextRetryAfterUtc = null;

        foreach (var t in detail.Tracks)
        {
            row.Tracks.Add(new CanonicalAlbumTrack
            {
                DiscNumber = t.DiscNumber,
                TrackNumber = t.TrackNumber,
                Title = t.Title,
                DurationMs = t.DurationMs > 0 ? t.DurationMs : null,
                CorroboratingProviders = nameof(EnrichmentProvider.Deezer),
                CorroborationCount = 1,
            });
        }

        // The tracks need their ids before the matcher can pair them with owned songs.
        await db.SaveChangesAsync(ct);
        return row;
    }

    private sealed record Claims(HashSet<(string, string)> Tracks, HashSet<string> SpotifyIds, HashSet<string> DeezerIds);

    private async Task<Claims> LoadClaimsAsync(Guid ownerId, CancellationToken ct)
    {
        var rows = await db.WishlistItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.OwnerUserId == ownerId)
            .Select(w => new { w.Artist, w.Title, w.Status, w.NextAttemptAtUtc, w.SpotifyTrackId, w.DeezerTrackId })
            .ToListAsync(ct);

        var claims = new Claims(new(), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        foreach (var row in rows)
        {
            // Both unique indexes hold whatever the status, so every id is spoken for.
            if (row.SpotifyTrackId is not null) claims.SpotifyIds.Add(row.SpotifyTrackId);
            if (row.DeezerTrackId is not null) claims.DeezerIds.Add(row.DeezerTrackId);

            var inFlight = row.Status != WishlistItemStatus.NotFound
                && (row.Status != WishlistItemStatus.Failed || row.NextAttemptAtUtc != null);
            if (inFlight && ClaimKey(row.Artist, row.Title) is { } key) claims.Tracks.Add(key);
        }

        return claims;
    }

    /// <summary>Spotify ids for the lossless provider, minus any id another wishlist row already holds.</summary>
    private async Task<Dictionary<int, string>> ResolveSpotifyIdsAsync(
        CanonicalAlbum canonical, IReadOnlyList<CanonicalAlbumTrack> tracks, HashSet<string> claimed, CancellationToken ct)
    {
        IReadOnlyDictionary<int, string> resolved;
        try
        {
            resolved = await identityResolver.ResolveSpotifyTrackIdsAsync(canonical, tracks, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve Spotify ids for {Album}", canonical.DisplayTitle);
            return [];
        }

        var usable = new Dictionary<int, string>(resolved.Count);
        foreach (var (trackId, spotifyId) in resolved)
        {
            if (claimed.Add(spotifyId)) usable[trackId] = spotifyId;
        }

        return usable;
    }

    /// <summary>
    /// The ISRC of each Deezer track Spotify could not identify — the lossless provider can still find
    /// a source from it. Bounded; a track past the bound simply falls through to the provider chain.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveIsrcsAsync(IEnumerable<string?> deezerIds, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in deezerIds.OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxIsrcLookups))
        {
            try
            {
                var track = await deezer.LookupByIdAsync(id, ct);
                if (!string.IsNullOrWhiteSpace(track?.Isrc)) found[id] = track!.Isrc!;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Deezer ISRC lookup failed for track {Id}", id);
                break;
            }
        }

        return found;
    }

    private static (string, string)? ClaimKey(string? artist, string? title)
    {
        var a = TitleNormalizer.NormalizeForSearch(artist);
        var t = TitleNormalizer.NormalizeForSearch(title);
        return a.Length == 0 || t.Length == 0 ? null : (a, t);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
