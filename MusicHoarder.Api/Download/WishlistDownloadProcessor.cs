using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Download;

/// <summary>
/// The testable core of the wishlist downloader: processes one batch of Pending items against a
/// provided <see cref="MusicHoarderDbContext"/> and links already-downloaded items to their ingested
/// library song. The <see cref="DownloadBackgroundService"/> owns the loop/scoping and delegates here.
/// </summary>
public class WishlistDownloadProcessor(
    IEnumerable<IDownloadProvider> downloadProviders,
    DownloadProgressTracker progressTracker,
    IMusicVideoDownloader musicVideoDownloader,
    MusicVideoChannel musicVideoChannel,
    IDownloadArtworkEmbedder artworkEmbedder,
    IOptions<MusicEnricherOptions> options,
    ILogger<WishlistDownloadProcessor> logger)
{
    /// <summary>
    /// Processes up to <see cref="MusicEnricherOptions.WishlistDownloadBatchSize"/> Pending items: skips
    /// exact already-owned tracks, downloads the rest via the resolved provider, and persists the resulting
    /// status transitions. Returns the number of items processed and how many produced a new file.
    /// </summary>
    public async Task<(int Processed, int Downloaded)> ProcessBatchAsync(
        MusicHoarderDbContext db, Guid ownerId, CancellationToken ct)
    {
        var opts = options.Value;
        var destinationDir = opts.DownloadDirectory;
        var batchSize = Math.Clamp(opts.WishlistDownloadBatchSize, 1, 500);

        var batch = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId)
            .ExcludingDemoTenant()
            .Where(w => w.Status == WishlistItemStatus.Pending)
            // Origin first: what the owner asked for is claimed strictly before album completion. This
            // has to be an explicit discriminator, not a timestamp — EF emits a plain ORDER BY DESC and
            // Postgres defaults to NULLS FIRST there, so a null SpotifyAddedAtUtc sorts to the *front*
            // (already true of Deezer-sourced rows). Relying on the timestamp would put background fill
            // ahead of the queue, and the EF in-memory provider orders nulls the other way, so a test
            // written against it would happily agree.
            .OrderBy(w => w.Origin)
            .ThenByDescending(w => w.SpotifyAddedAtUtc)
            .ThenBy(w => w.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return (0, 0);

        // Only Spotify-keyed items participate in the in-library match cache; Deezer-sourced items with a
        // null Spotify id simply have no cache row and go straight to download.
        var trackIds = batch.Where(w => w.SpotifyTrackId != null).Select(w => w.SpotifyTrackId!).ToList();
        var matches = await db.SpotifyTrackLibraryMatches
            .IgnoreQueryFilters()
            .Where(m => m.OwnerUserId == ownerId && trackIds.Contains(m.SpotifyTrackId))
            .ToDictionaryAsync(m => m.SpotifyTrackId, ct);

        var providers = ResolveProviders();
        var inLibrary = (int)ComparisonMatchStatus.InLibrary;
        var now = DateTime.UtcNow;

        // Partition: tracks with an exact in-library match are skipped (no download); the rest go out.
        var owned = new List<(WishlistItem Item, int? SongId)>();
        var toDownload = new List<WishlistItem>();
        foreach (var item in batch)
        {
            if (item.SpotifyTrackId is { } sid && matches.TryGetValue(sid, out var match) && match.MatchStatus == inLibrary)
                owned.Add((item, match.MatchedSongId));
            else
                toDownload.Add(item);
        }

        foreach (var (item, songId) in owned)
        {
            item.Status = WishlistItemStatus.SkippedOwned;
            item.DownloadedSongId = songId;
            item.UpdatedAtUtc = now;
            progressTracker.IncrementSkipped();
        }

        // A SkippedOwned item links straight to an existing song without going through the linker, so
        // the intent has to be recomputed here too — this is how an album-fill track the owner later
        // likes on Spotify gets promoted to Explicit and appears in "My music". Persist the new links
        // first: the recompute reads them back from the store, so an in-flight DownloadedSongId would
        // be invisible and the song would stay AlbumFill.
        if (owned.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await ApplyAcquisitionIntentAsync(db, ownerId, owned.Select(o => o.SongId).OfType<int>(), ct);
        }

        // Surface in-flight work: persist Downloading before the (slow) fetch so the UI's Downloading
        // tab/badge reflects what's actually running, instead of items jumping straight to a terminal
        // state. Unresolved items (run cancelled mid-batch) are reverted to Pending below, and a crash
        // leaves them Downloading until ResetStaleDownloadingAsync reclaims them on the next run.
        if (toDownload.Count > 0)
        {
            foreach (var item in toDownload)
            {
                item.Status = WishlistItemStatus.Downloading;
                item.UpdatedAtUtc = now;
            }
            await db.SaveChangesAsync(ct);
        }

        // Downloads hit the network/disk only — no DB access — so the parallel section is EF-safe.
        // Parallel.ForEachAsync already bounds in-flight bodies to MaxDegreeOfParallelism, so no extra
        // semaphore is needed.
        var results = new Dictionary<int, (DownloadResult Result, string ProviderName, MusicVideoDownloadResult? Video)>();
        var resultsLock = new object();
        if (toDownload.Count > 0)
        {
            await Parallel.ForEachAsync(
                toDownload,
                new ParallelOptions { MaxDegreeOfParallelism = opts.DownloadConcurrency, CancellationToken = ct },
                async (item, token) =>
                {
                    var req = new DownloadRequest(item.Artist, item.Title, item.Album, item.Isrc, item.DurationMs, destinationDir, item.SpotifyTrackId, item.SourceUrl);
                    // Provider chain: fall through to the next provider only on NotFound. A transient
                    // Error stops the chain — the item goes Failed and the next sweep retries from the
                    // top, so a flaky first provider can't silently burn the fallback's quota.
                    var result = DownloadResult.Missing("no download provider configured");
                    var providerName = providers.Count > 0 ? providers[0].Name : "";
                    foreach (var candidate in providers)
                    {
                        providerName = candidate.Name;
                        result = await candidate.DownloadAsync(req, token);
                        if (result.Success || !result.NotFound)
                            break;
                    }
                    // Stamp the authoritative Spotify identity onto the file so the scanner reads it as
                    // the source identity (instead of the downloader's native YouTube tags). Disk-only,
                    // so it stays inside the DB-free parallel section. Tolerant: a stamp failure leaves
                    // the download intact and just falls back to whatever tags the file carries.
                    if (result.Success && result.FilePath is not null)
                    {
                        // ResolveAlbum: an item that reached the downloader without an album (a pasted
                        // YouTube video) is filed as a single named after the track — a blank ALBUM tag
                        // is what routes a build into a shared "Unknown Album" folder.
                        DownloadTagWriter.Stamp(
                            result.FilePath, item.Artist, item.Title,
                            DownloadTagWriter.ResolveAlbum(item.Album, item.Title), item.Isrc, logger);
                        // Give the file the artwork of the identity it was requested for (Spotify album
                        // image / YouTube thumbnail). yt-dlp embeds nothing, and the build's cover pass
                        // searches the external providers by album — which finds nothing for a one-off
                        // single. Network + disk only, and never fails the download.
                        await artworkEmbedder.EmbedAsync(result.FilePath, item.AlbumArt, token);
                    }
                    // Companion music video: a second yt-dlp fetch, pinned to the exact video the audio
                    // came from when the audio provider knows it (SourceId), else the item's pasted
                    // YouTube URL, else a search. The pin is provenance (not explicit), so an
                    // audio-only source upload gets swapped for a real music video by search. Network/
                    // disk only, so it stays in the DB-free section. A video failure never fails the
                    // item — the audio is the product. The item's own import-dialog choice overrides
                    // the server default in both directions.
                    MusicVideoDownloadResult? video = null;
                    if ((item.DownloadMusicVideo ?? opts.DownloadMusicVideos) && result.Success)
                        video = await musicVideoDownloader.DownloadAsync(
                            new MusicVideoFetchRequest(
                                result.SourceId ?? item.SourceUrl, PinIsExplicit: false,
                                item.Artist, item.Title, item.DurationMs),
                            token);
                    lock (resultsLock) results[item.Id] = (result, providerName, video);
                });
        }

        // Re-read the clock after the (potentially minutes-long, throttled) fetch so terminal
        // UpdatedAtUtc stamps reflect when each item actually finished, not when the batch started.
        var finishedAt = DateTime.UtcNow;
        var downloadedCount = 0;
        foreach (var item in toDownload)
        {
            if (!results.TryGetValue(item.Id, out var entry))
            {
                // Cancelled before this item ran — revert the optimistic Downloading mark to Pending
                // so the next run retries it (without inflating AttemptCount).
                item.Status = WishlistItemStatus.Pending;
                item.UpdatedAtUtc = finishedAt;
                continue;
            }

            var (result, providerName, video) = entry;
            item.DownloadProvider = providerName;
            item.AttemptCount += 1;
            item.UpdatedAtUtc = finishedAt;

            if (result.Success && result.FilePath is not null)
            {
                item.Status = WishlistItemStatus.Downloaded;
                item.DownloadedFilePath = NormalizePath(result.FilePath);
                item.LastError = null;
                if (video is { Success: true, FilePath: not null })
                {
                    item.DownloadedVideoFilePath = NormalizePath(video.FilePath);
                    item.DownloadedVideoYouTubeId = video.YouTubeVideoId;
                    // Offset 0 by construction only when the audio was extracted from this very video.
                    item.DownloadedVideoIsSameSource =
                        result.SourceId is not null && video.YouTubeVideoId == result.SourceId;
                }
                downloadedCount++;
                progressTracker.IncrementDownloaded();
            }
            else if (result.NotFound)
            {
                item.Status = WishlistItemStatus.NotFound;
                item.LastError = result.Error;
                progressTracker.IncrementNotFound();
            }
            else
            {
                item.Status = WishlistItemStatus.Failed;
                item.LastError = result.Error;
                progressTracker.IncrementFailed();
            }
        }

        await db.SaveChangesAsync(ct);
        return (batch.Count, downloadedCount);
    }

    /// <summary>
    /// Links Downloaded items to the library song the scanner created for their file (matching
    /// <see cref="SongMetadata.SourcePath"/> to <see cref="WishlistItem.DownloadedFilePath"/>). Also
    /// heals items whose previously-linked song was soft-deleted: re-links to a live song if the file
    /// was re-scanned, otherwise clears the now-dangling link (a soft-delete leaves the FK intact since
    /// the row isn't physically removed, so this is the only place the stale reference gets cleaned up).
    /// Returns how many were newly linked.
    /// </summary>
    public async Task<int> LinkDownloadedItemsAsync(MusicHoarderDbContext db, Guid ownerId, CancellationToken ct)
    {
        var unlinked = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId
                && w.Status == WishlistItemStatus.Downloaded
                && w.DownloadedFilePath != null
                && (w.DownloadedSongId == null
                    || (w.DownloadedSong != null && w.DownloadedSong.DeletedAtUtc != null)))
            .ToListAsync(ct);

        if (unlinked.Count == 0) return 0;

        var paths = unlinked.Select(w => w.DownloadedFilePath!).ToList();
        var songs = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && s.DeletedAtUtc == null && paths.Contains(s.SourcePath))
            .Select(s => new { s.Id, s.SourcePath })
            .ToListAsync(ct);
        var byPath = songs
            .GroupBy(s => s.SourcePath)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var linked = 0;
        var changed = false;
        var now = DateTime.UtcNow;
        foreach (var item in unlinked)
        {
            if (item.DownloadedFilePath is { } p && byPath.TryGetValue(p, out var songId))
            {
                if (item.DownloadedSongId == songId) continue; // already linked to the live song
                item.DownloadedSongId = songId;
                item.UpdatedAtUtc = now;
                linked++;
                changed = true;
            }
            else if (item.DownloadedSongId != null)
            {
                // Was linked to a since-soft-deleted song and no live song exists at the path anymore —
                // drop the dangling link so the row reads "downloaded, not in library" instead of
                // pointing at an invisible song (and so it re-links if the file is re-scanned later).
                item.DownloadedSongId = null;
                item.UpdatedAtUtc = now;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            if (linked > 0)
                logger.LogInformation("Linked {Count} downloaded wishlist items to library songs", linked);
        }

        if (linked > 0)
        {
            await ApplyAcquisitionIntentAsync(
                db, ownerId, unlinked.Select(w => w.DownloadedSongId).OfType<int>(), ct);
        }

        await PromoteDownloadedVideosAsync(db, unlinked, ct);

        return linked;
    }

    /// <summary>
    /// Promotes a video carried on a now-linked wishlist item into the song's
    /// <see cref="SongMusicVideo"/> row. Same-source videos (audio extracted from that exact video)
    /// are Ready with offset 0 by construction; anything else starts <c>Unaligned</c> and an Align
    /// work item estimates the real offset in the background. Idempotent: a song that already has a
    /// video row is left alone.
    /// </summary>
    private async Task PromoteDownloadedVideosAsync(
        MusicHoarderDbContext db, List<WishlistItem> items, CancellationToken ct)
    {
        var candidates = items
            .Where(w => w.DownloadedSongId != null && w.DownloadedVideoFilePath != null)
            .GroupBy(w => w.DownloadedSongId!.Value)
            .Select(g => g.First())
            .ToList();
        if (candidates.Count == 0) return;

        var songIds = candidates.Select(w => w.DownloadedSongId!.Value).ToList();
        var existing = await db.SongMusicVideos
            .IgnoreQueryFilters()
            .Where(v => songIds.Contains(v.SongId))
            .Select(v => v.SongId)
            .ToListAsync(ct);
        var existingSet = existing.ToHashSet();

        var toAlign = new List<int>();
        var now = DateTime.UtcNow;
        foreach (var item in candidates)
        {
            var songId = item.DownloadedSongId!.Value;
            if (existingSet.Contains(songId)) continue;
            if (!File.Exists(item.DownloadedVideoFilePath!)) continue;

            db.SongMusicVideos.Add(new SongMusicVideo
            {
                SongId = songId,
                FilePath = item.DownloadedVideoFilePath,
                YouTubeVideoId = item.DownloadedVideoYouTubeId,
                Status = MusicVideoStatus.Ready,
                SyncOffsetMs = 0,
                SyncSource = item.DownloadedVideoIsSameSource
                    ? MusicVideoSyncSource.SameSource
                    : MusicVideoSyncSource.Unaligned,
                FetchedAtUtc = now,
            });
            // Every promoted video visits the worker: it fetches the YouTube thumbnail (the artless-
            // song cover fallback) and estimates the sync offset — the latter skipped for SameSource
            // rows, whose offset is exact by construction.
            toAlign.Add(songId);
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            foreach (var songId in toAlign)
                musicVideoChannel.Enqueue(new MusicVideoWorkItem(songId, MusicVideoWorkKind.Align));
        }
    }

    /// <summary>
    /// Recomputes <see cref="SongMetadata.AcquisitionIntent"/> for the given songs from every wishlist
    /// item pointing at them. This is the single writer of that column, and the only place a song
    /// becomes <see cref="SongAcquisitionIntent.AlbumFill"/>.
    /// <para>
    /// <b>Explicit is absorbing.</b> A song is AlbumFill only when <em>every</em> link is an album-fill
    /// item; one <see cref="WishlistItemOrigin.UserRequested"/> link makes it Explicit and it never goes
    /// back. That single rule covers the case that matters: an album-fill track the owner later likes on
    /// Spotify gets a second, user-requested wishlist row, and the song moves into "My music" the moment
    /// the owner expresses intent — no special-casing anywhere.
    /// </para>
    /// </summary>
    private static async Task ApplyAcquisitionIntentAsync(
        MusicHoarderDbContext db, Guid ownerId, IEnumerable<int> songIds, CancellationToken ct)
    {
        var ids = songIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var links = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.DownloadedSongId != null && ids.Contains(w.DownloadedSongId!.Value))
            .Select(w => new { SongId = w.DownloadedSongId!.Value, w.Origin })
            .ToListAsync(ct);

        if (links.Count == 0) return;

        var intentBySong = links
            .GroupBy(l => l.SongId)
            .ToDictionary(
                g => g.Key,
                g => g.All(l => l.Origin == WishlistItemOrigin.AlbumCompletion)
                    ? SongAcquisitionIntent.AlbumFill
                    : SongAcquisitionIntent.Explicit);

        var songs = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId && intentBySong.Keys.Contains(s.Id))
            .ToListAsync(ct);

        // Songs with no wishlist link at all (scanned, synced) never appear in intentBySong, so they
        // keep the Explicit default untouched — which is what makes this recomputation safe to re-run.
        var changed = false;
        foreach (var song in songs)
        {
            if (!intentBySong.TryGetValue(song.Id, out var intent)) continue;
            if (song.AcquisitionIntent == intent) continue;
            song.AcquisitionIntent = intent;
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reverts items stuck in <see cref="WishlistItemStatus.Downloading"/> back to Pending. Items are
    /// only transiently Downloading during an active batch, so any found between runs are leftovers
    /// from a crash/restart mid-fetch — reclaim them so they retry. Returns how many were reset.
    /// </summary>
    public async Task<int> ResetStaleDownloadingAsync(MusicHoarderDbContext db, Guid ownerId, CancellationToken ct)
    {
        var stale = await db.WishlistItems
            .IgnoreQueryFilters()
            .Where(w => w.OwnerUserId == ownerId && w.Status == WishlistItemStatus.Downloading)
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var item in stale)
        {
            item.Status = WishlistItemStatus.Pending;
            item.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Reset {Count} stale Downloading wishlist items to Pending", stale.Count);
        return stale.Count;
    }

    /// <summary>
    /// Resolves the ordered provider chain from <see cref="MusicEnricherOptions.DownloadProviders"/>
    /// (falling back to the legacy single <see cref="MusicEnricherOptions.DownloadProvider"/>).
    /// Unknown names are skipped with a warning; an empty result falls back to the first registered
    /// provider so the worker never runs with nothing.
    /// </summary>
    public IReadOnlyList<IDownloadProvider> ResolveProviders()
    {
        var resolved = DownloadProviderChain.Resolve(
            DownloadProviderChain.Names(options.Value), downloadProviders, p => p.Name, logger);
        // An empty/unknown chain still needs something to run: fall back to the first registered
        // provider (historically yt-dlp).
        if (resolved.Count == 0)
            resolved.Add(downloadProviders.First());
        return resolved;
    }

    /// <summary>Normalize to forward slashes to match how the scanner stores <see cref="SongMetadata.SourcePath"/>.</summary>
    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
