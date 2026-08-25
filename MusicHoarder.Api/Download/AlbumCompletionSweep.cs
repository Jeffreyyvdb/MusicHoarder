using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Download;

/// <summary>
/// What one pass of album completion did. Returned rather than logged-and-forgotten because the
/// owner needs an answer the moment they turn the feature on: with a threshold of one owned track
/// and a per-pass cap, "queued nothing" is a perfectly normal outcome, and without saying *why* it
/// is indistinguishable from the feature being broken.
/// </summary>
/// <param name="IdleReason">
/// Set when the pass stopped before examining anything — one of the <c>Idle*</c> constants. Null
/// when the sweep genuinely ran, even if it queued nothing.
/// </param>
public readonly record struct AlbumCompletionSweepResult(
    int AlbumsExamined,
    int TracksQueued,
    int AlbumsFilled,
    int AlbumsSkipped,
    int AlbumsAlreadyComplete,
    string? IdleReason = null)
{
    /// <summary>The wishlist downloader is off, so queueing tracks nothing will fetch is pointless.</summary>
    public const string IdleDownloadsDisabled = "downloads-disabled";

    /// <summary>Album completion itself is switched off.</summary>
    public const string IdleDisabled = "disabled";

    /// <summary>Enough album-fill items are still pending; discovery waits for the downloader to catch up.</summary>
    public const string IdleAtPendingCeiling = "at-pending-ceiling";

    /// <summary>No album is due for a look — everything eligible has been swept recently.</summary>
    public const string IdleNoCandidates = "no-candidates";

    /// <summary>Nothing in the library has a reconciled canonical tracklist to compare against yet.</summary>
    public const string IdleNoCanonicalAlbums = "no-canonical-albums";

    /// <summary>There is no music to complete albums for.</summary>
    public const string IdleLibraryEmpty = "library-empty";

    public static AlbumCompletionSweepResult Idle(string reason) => new(0, 0, 0, 0, 0, reason);
}

/// <summary>
/// One pass of album completion: for each album the owner already holds part of, compare what they
/// have against the reconciled <see cref="CanonicalAlbum"/> tracklist and queue the missing tracks as
/// <see cref="WishlistItemOrigin.AlbumCompletion"/> wishlist items. Everything downstream — the
/// downloader, the staging directory, the scanner, enrichment, the builder — is the existing wishlist
/// pipeline, unchanged.
/// <para>
/// Each queued track is handed to <see cref="IAlbumCompletionIdentityResolver"/> first: a canonical
/// track carries no streaming id, and without one the lossless provider cannot acquire it and the fill
/// silently degrades to the yt-dlp floor.
/// </para>
/// <para>
/// The threshold is one owned track, so the candidate set is effectively the whole library. Three
/// independent throttles keep that from becoming a flood: a per-sweep album cap, a per-album track
/// cap, and — the one that actually matters — a ceiling on pending album-completion items, which
/// bounds the queue against the downloader's real throughput rather than against wall-clock.
/// </para>
/// </summary>
public class AlbumCompletionSweep(
    MusicHoarderDbContext db,
    IOwnerLookupService ownerLookup,
    IRuntimeSettingsService runtimeSettings,
    IAlbumCompletionIdentityResolver identityResolver,
    IOptions<MusicEnricherOptions> options,
    ILogger<AlbumCompletionSweep> logger)
{
    /// <summary>Owned-song fields the sweep needs, projected before grouping.</summary>
    private sealed record SongRow(
        int Id,
        string? AlbumArtist,
        string? Artist,
        string? Album,
        string? MusicBrainzId,
        int? DiscNumber,
        int? TrackNumber,
        string? Title,
        bool IsCompilation,
        string? ReleaseTypes,
        EnrichmentStatus EnrichmentStatus);

    /// <summary>Queues the missing tracks of eligible albums and reports what it decided.</summary>
    public async Task<AlbumCompletionSweepResult> SweepAsync(CancellationToken ct)
    {
        var opts = options.Value;

        // Album completion rides the wishlist downloader, so it is meaningless without one. The
        // owner-facing on/off is the runtime overlay (config supplies its default), matching how
        // AutoDownloadWishlist works — so the Settings toggle takes effect without a redeploy.
        if (!opts.EnableWishlistDownloads)
            return AlbumCompletionSweepResult.Idle(AlbumCompletionSweepResult.IdleDownloadsDisabled);

        var effective = await runtimeSettings.GetAsync(ct);
        if (!effective.AlbumCompletionEnabled)
            return AlbumCompletionSweepResult.Idle(AlbumCompletionSweepResult.IdleDisabled);

        var ownerId = ownerLookup.OwnerUserId;

        // Backpressure first, before any query work: while the downloader is still chewing through the
        // last batch there is nothing useful to discover.
        if (opts.AlbumCompletionMaxPendingItems > 0)
        {
            var pending = await db.WishlistItems
                .IgnoreQueryFilters()
                .CountAsync(w => w.OwnerUserId == ownerId
                    && w.Origin == WishlistItemOrigin.AlbumCompletion
                    && w.Status == WishlistItemStatus.Pending, ct);
            if (pending >= opts.AlbumCompletionMaxPendingItems)
            {
                logger.LogDebug(
                    "Album completion idle: {Pending} pending item(s) already at the {Max} ceiling",
                    pending, opts.AlbumCompletionMaxPendingItems);
                return AlbumCompletionSweepResult.Idle(AlbumCompletionSweepResult.IdleAtPendingCeiling);
            }
        }

        // Materialize before grouping — the EF in-memory provider can't translate this GroupBy. Same
        // preamble as CanonicalAlbumFetchService, which builds the very keys we join on.
        var songs = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId)
            .ExcludingDemoTenant()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate
                && s.Album != null && s.Album != "")
            .Select(s => new SongRow(
                s.Id, s.AlbumArtist, s.Artist, s.Album, s.MusicBrainzId, s.DiscNumber, s.TrackNumber,
                s.Title, s.IsCompilation, s.ReleaseTypes, s.EnrichmentStatus))
            .ToListAsync(ct);

        if (songs.Count == 0)
            return AlbumCompletionSweepResult.Idle(AlbumCompletionSweepResult.IdleLibraryEmpty);

        var groups = songs
            .Select(s => (Song: s, ArtistRaw: s.AlbumArtist ?? s.Artist, Album: s.Album!))
            .Where(x => !string.IsNullOrWhiteSpace(x.ArtistRaw))
            .GroupBy(x => (
                ArtistKey: TitleNormalizer.NormalizeForSearch(x.ArtistRaw),
                AlbumKey: TitleNormalizer.NormalizeForSearch(x.Album)))
            .Where(g => g.Key.ArtistKey.Length > 0 && g.Key.AlbumKey.Length > 0
                // A canonical album only exists for groups with a matched song, so anything else can't
                // be joined anyway. The whole group still feeds the owned-track matcher below: an
                // unmatched file sitting in the folder is still owned and must not be re-downloaded.
                && g.Any(x => x.Song.EnrichmentStatus == EnrichmentStatus.Matched))
            .ToList();

        if (groups.Count == 0)
            return AlbumCompletionSweepResult.Idle(AlbumCompletionSweepResult.IdleNoCanonicalAlbums);

        var artistKeys = groups.Select(g => g.Key.ArtistKey).Distinct().ToList();
        var canonicalAlbums = await db.CanonicalAlbums
            .Include(a => a.Tracks)
            .Where(a => a.Status == CanonicalAlbumStatus.Fetched && artistKeys.Contains(a.ArtistKey))
            .ToListAsync(ct);
        var canonicalByKey = canonicalAlbums.ToDictionary(a => (a.ArtistKey, a.AlbumKey));

        var markers = await db.AlbumCompletionStates
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId)
            .ToListAsync(ct);
        var markerByAlbumId = markers.ToDictionary(m => m.CanonicalAlbumId);

        var now = DateTime.UtcNow;

        var linked = groups
            .Select(g => (Group: g, Canonical: canonicalByKey.GetValueOrDefault(g.Key)))
            .Where(x => x.Canonical is not null)
            .ToList();

        // Worth telling apart from "nothing due": on a young library CanonicalAlbumFetchService simply
        // hasn't caught up yet, and the honest answer is "ask again shortly", not "nothing to do".
        if (linked.Count == 0)
            return AlbumCompletionSweepResult.Idle(AlbumCompletionSweepResult.IdleNoCanonicalAlbums);

        var candidates = linked
            .Where(x => NeedsSweep(x.Canonical!, markerByAlbumId.GetValueOrDefault(x.Canonical!.Id), now))
            // Closest to complete first, so a multi-day drain finishes albums instead of scattering.
            .OrderByDescending(x => x.Group.Count())
            .ThenBy(x => x.Canonical!.Id)
            .Take(Math.Clamp(opts.AlbumCompletionAlbumsPerSweep, 1, 100))
            .ToList();

        if (candidates.Count == 0)
            return AlbumCompletionSweepResult.Idle(AlbumCompletionSweepResult.IdleNoCandidates);

        var enqueued = 0;
        var filled = 0;
        var skipped = 0;
        var alreadyComplete = 0;
        // (OwnerUserId, SpotifyTrackId) is uniquely indexed, and rows added for earlier albums in this
        // pass are not visible to a query until the single SaveChanges below — so claims are tracked
        // across the whole sweep, not per album.
        var claimedSpotifyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (group, canonical) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var (queued, status) = await ProcessAlbumAsync(
                ownerId, group.Select(x => x.Song).ToList(), canonical!,
                markerByAlbumId.GetValueOrDefault(canonical!.Id), opts, now, claimedSpotifyIds, ct);

            enqueued += queued;
            switch (status)
            {
                case AlbumCompletionStatus.Filled: filled++; break;
                case AlbumCompletionStatus.Skipped: skipped++; break;
                default: alreadyComplete++; break;
            }
        }

        await db.SaveChangesAsync(ct);

        if (enqueued > 0)
        {
            // Deliberately no jobManager.TryStartJob(JobType.Download): TryStartJob clears the pause
            // flag because it models a user action, and a background drip has no business resurrecting
            // a step the owner paused. DownloadBackgroundService picks Pending items up on its own tick.
            logger.LogInformation(
                "Album completion queued {Count} missing track(s) across {Albums} album(s)",
                enqueued, candidates.Count);
        }

        return new AlbumCompletionSweepResult(
            AlbumsExamined: candidates.Count,
            TracksQueued: enqueued,
            AlbumsFilled: filled,
            AlbumsSkipped: skipped,
            AlbumsAlreadyComplete: alreadyComplete);
    }

    /// <summary>
    /// Never swept, the canonical album has been re-fetched since the last look (a later edition may
    /// have added tracks), or the revisit timer has come due. A skipped album has a null timer and so
    /// only ever returns via a re-fetch — a compilation does not stop being one.
    /// </summary>
    private static bool NeedsSweep(CanonicalAlbum canonical, AlbumCompletionState? marker, DateTime now)
    {
        if (marker is null) return true;
        if (canonical.FetchedAtUtc is { } fetched && fetched > marker.LastSweptAtUtc) return true;
        return marker.NextSweepAfterUtc is { } next && next <= now;
    }

    private async Task<(int Queued, AlbumCompletionStatus Status)> ProcessAlbumAsync(
        Guid ownerId,
        IReadOnlyList<SongRow> owned,
        CanonicalAlbum canonical,
        AlbumCompletionState? marker,
        MusicEnricherOptions opts,
        DateTime now,
        HashSet<string> claimedSpotifyIds,
        CancellationToken ct)
    {
        var candidateFacts = owned
            .Select(s => new AlbumCompletionCandidate(s.AlbumArtist, s.Artist, s.IsCompilation, s.ReleaseTypes))
            .ToList();

        var skipReason = AlbumCompletionEligibility.Skip(candidateFacts, canonical, opts);
        if (skipReason is not null)
        {
            UpsertMarker(ownerId, canonical, marker, AlbumCompletionStatus.Skipped, owned.Count, 0, skipReason, now);
            return (0, AlbumCompletionStatus.Skipped);
        }

        var orderedTracks = canonical.Tracks
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToList();

        // The same call the album page makes, so "missing" means the same thing in both places.
        var ownedInfos = owned
            .Select(s => new OwnedTrackInfo(s.Id, s.MusicBrainzId, s.DiscNumber, s.TrackNumber, s.Title))
            .ToList();
        var matched = AlbumOwnedTrackMatcher.Match(orderedTracks, ownedInfos, opts.IdentityTitleThreshold);

        // Every item ever created for this album, any status. Failed/NotFound are terminal — nothing
        // resets them — so they act as permanent tombstones and cost nothing to honour.
        var existing = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.CanonicalAlbumId == canonical.Id)
            .Select(w => w.Title)
            .ToListAsync(ct);
        var existingKeys = existing
            .Select(TitleNormalizer.NormalizeForSearch)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<CanonicalAlbumTrack>();
        foreach (var track in orderedTracks)
        {
            if (matched.ContainsKey(track.Id)) continue;
            if (string.IsNullOrWhiteSpace(track.Title)) continue;
            // Not every provider in the winning cluster backs a contested track, so it's usually a
            // deluxe/bonus phantom that no downloader will find — pure NotFound noise.
            if (opts.AlbumCompletionSkipContestedTracks && track.IsContested) continue;

            var key = TitleNormalizer.NormalizeForSearch(track.Title);
            if (key.Length > 0 && existingKeys.Contains(key)) continue;

            missing.Add(track);
        }

        if (missing.Count == 0)
        {
            UpsertMarker(ownerId, canonical, marker, AlbumCompletionStatus.NothingMissing, owned.Count, 0, null, now);
            return (0, AlbumCompletionStatus.NothingMissing);
        }

        var take = Math.Clamp(opts.AlbumCompletionMaxTracksPerAlbum, 1, 500);
        if (missing.Count > take)
        {
            logger.LogInformation(
                "Album completion capped {Album} at {Take} of {Total} missing track(s) (AlbumCompletionMaxTracksPerAlbum)",
                canonical.DisplayTitle, take, missing.Count);
            missing = missing.Take(take).ToList();
        }

        // A canonical track carries no artist of its own, so every filled track is searched under the
        // album artist. That is correct on a real album and catastrophic on a compilation — which is
        // exactly what AlbumCompletionEligibility refuses to let through.
        var artist = !string.IsNullOrWhiteSpace(canonical.DisplayArtist)
            ? canonical.DisplayArtist!
            : owned.Select(s => s.AlbumArtist ?? s.Artist).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? string.Empty;
        var album = !string.IsNullOrWhiteSpace(canonical.DisplayTitle)
            ? canonical.DisplayTitle
            : owned.Select(s => s.Album).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

        // The identity the download chain needs. Resolved here, once per album, rather than at download
        // time: the canonical tracklist and its Spotify counterpart are album-shaped, so one lookup
        // identifies every missing track — and a queued item that already carries the id survives a
        // later retry without asking Spotify again.
        var spotifyIds = await ResolveSpotifyIdsAsync(ownerId, canonical, missing, claimedSpotifyIds, ct);

        foreach (var track in missing)
        {
            db.WishlistItems.Add(new WishlistItem
            {
                OwnerUserId = ownerId,
                // No WishlistSource: that models a remote collection with a sync loop, which this isn't.
                WishlistSourceId = null,
                Origin = WishlistItemOrigin.AlbumCompletion,
                CanonicalAlbumId = canonical.Id,
                Title = Truncate(track.Title!, 512),
                Artist = Truncate(artist, 512),
                Album = album is null ? null : Truncate(album, 512),
                DurationMs = track.DurationMs ?? 0,
                AlbumArt = canonical.CoverArtUrl is null ? null : Truncate(canonical.CoverArtUrl, 1024),
                // Not "the owner saved this on Spotify" — it is the acquisition key the lossless
                // provider resolves to a source. Null when unresolvable; the chain then falls through.
                SpotifyTrackId = spotifyIds.GetValueOrDefault(track.Id),
                // Means "when the owner saved it on Spotify", and they never did. Left null rather than
                // faked; the Origin discriminator is what orders the download queue.
                SpotifyAddedAtUtc = null,
                Status = WishlistItemStatus.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        UpsertMarker(ownerId, canonical, marker, AlbumCompletionStatus.Filled, owned.Count, missing.Count, null, now);
        return (missing.Count, AlbumCompletionStatus.Filled);
    }

    /// <summary>
    /// Spotify track ids for the tracks about to be queued, minus any id already spoken for. One
    /// wishlist row per (owner, Spotify track) is a unique index, and a fill track can legitimately
    /// resolve to a track the owner already wishlisted from Spotify — that row is the better claimant,
    /// so this one is simply left unidentified rather than colliding.
    /// </summary>
    private async Task<Dictionary<int, string>> ResolveSpotifyIdsAsync(
        Guid ownerId,
        CanonicalAlbum canonical,
        IReadOnlyList<CanonicalAlbumTrack> missing,
        HashSet<string> claimedSpotifyIds,
        CancellationToken ct)
    {
        var resolved = await identityResolver.ResolveSpotifyTrackIdsAsync(canonical, missing, ct);
        if (resolved.Count == 0)
            return [];

        var ids = resolved.Values.Distinct(StringComparer.Ordinal).ToList();
        var taken = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.SpotifyTrackId != null && ids.Contains(w.SpotifyTrackId))
            .Select(w => w.SpotifyTrackId!)
            .ToListAsync(ct);
        claimedSpotifyIds.UnionWith(taken);

        var usable = new Dictionary<int, string>(resolved.Count);
        foreach (var (trackId, spotifyId) in resolved)
        {
            if (claimedSpotifyIds.Add(spotifyId))
                usable[trackId] = spotifyId;
        }

        return usable;
    }

    private void UpsertMarker(
        Guid ownerId,
        CanonicalAlbum canonical,
        AlbumCompletionState? marker,
        AlbumCompletionStatus status,
        int ownedCount,
        int enqueuedCount,
        string? skipReason,
        DateTime now)
    {
        if (marker is null)
        {
            marker = new AlbumCompletionState
            {
                OwnerUserId = ownerId,
                CanonicalAlbumId = canonical.Id,
                CreatedAtUtc = now,
            };
            db.AlbumCompletionStates.Add(marker);
        }

        marker.Status = status;
        marker.LastSweptAtUtc = now;
        // A skip has no timer: it's a property of the release, not of the moment. Only a re-fetched
        // canonical album can bring it back.
        marker.NextSweepAfterUtc = status == AlbumCompletionStatus.Skipped
            ? null
            : now.AddDays(Math.Clamp(options.Value.AlbumCompletionRevisitDays, 0, 3650));
        marker.OwnedTrackCount = ownedCount;
        marker.CanonicalTrackCount = canonical.Tracks.Count;
        marker.EnqueuedTrackCount = enqueuedCount;
        marker.SkipReason = skipReason;
        marker.UpdatedAtUtc = now;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
