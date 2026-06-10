using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>Outcome of a single-album consolidation pass.</summary>
public sealed record CanonicalConsolidationResult(
    bool CanonicalFound, int Matched, int Corrected, int Requeued, int Unmatched);

public interface ICanonicalAlbumConsolidator
{
    /// <summary>
    /// When a Fetched <see cref="CanonicalAlbum"/> exists for <paramref name="artist"/>/<paramref name="album"/>,
    /// rewrites each owned song's album title/year and track/disc number from the canonical tracklist
    /// (matched by recording-MBID + fuzzy title, never by the owned — possibly corrupt — position),
    /// re-queuing already-built tracks so the next build relocates them into one folder with correct
    /// numbers. Mutates tracked entities and records change-log rows but does NOT call SaveChanges —
    /// the caller owns the unit of work. Returns <c>CanonicalFound=false</c> (no other side effects)
    /// when there is no canonical album, so the caller can fall back to a plain re-tag.
    /// </summary>
    Task<CanonicalConsolidationResult> ConsolidateAsync(
        MusicHoarderDbContext db, string artist, string album, CancellationToken ct);
}

public sealed class CanonicalAlbumConsolidator(
    IOptions<MusicEnricherOptions> options,
    ILogger<CanonicalAlbumConsolidator> logger) : ICanonicalAlbumConsolidator
{
    public async Task<CanonicalConsolidationResult> ConsolidateAsync(
        MusicHoarderDbContext db, string artist, string album, CancellationToken ct)
    {
        var artistKey = TitleNormalizer.NormalizeForSearch(artist);
        var albumKey = TitleNormalizer.NormalizeForSearch(album);
        if (artistKey.Length == 0 || albumKey.Length == 0)
            return new CanonicalConsolidationResult(false, 0, 0, 0, 0);

        var canonical = await db.CanonicalAlbums
            .Include(a => a.Tracks)
            .FirstOrDefaultAsync(a => a.ArtistKey == artistKey && a.AlbumKey == albumKey, ct);

        if (canonical is null || canonical.Status != CanonicalAlbumStatus.Fetched)
            return new CanonicalConsolidationResult(false, 0, 0, 0, 0);

        // Owned songs in this album group, mirroring the album view's (albumArtist ?? artist, album)
        // grouping. Tracked (no AsNoTracking) because we mutate them. The per-user query filter scopes
        // this to the caller's library.
        var artistLower = artist.ToLowerInvariant();
        var albumLower = album.ToLowerInvariant();
        var ownedSongs = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate)
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched)
            .Where(s => s.Album != null && s.Album.ToLower() == albumLower
                && ((s.AlbumArtist ?? s.Artist) ?? "").ToLower() == artistLower)
            .ToListAsync(ct);

        if (ownedSongs.Count == 0)
            return new CanonicalConsolidationResult(true, 0, 0, 0, 0);

        var orderedTracks = canonical.Tracks
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToList();

        var ownedInfo = ownedSongs
            .Select(s => new OwnedTrackInfo(s.Id, s.MusicBrainzId, s.DiscNumber, s.TrackNumber, s.Title))
            .ToList();

        // Position-free: the owned track numbers are exactly what's corrupt here.
        var trackToSong = AlbumOwnedTrackMatcher.Match(
            orderedTracks, ownedInfo, options.Value.IdentityTitleThreshold, usePositionPhase: false);

        var songToTrack = new Dictionary<int, CanonicalAlbumTrack>();
        foreach (var t in orderedTracks)
            if (trackToSong.TryGetValue(t.Id, out var songId))
                songToTrack[songId] = t;

        var now = DateTime.UtcNow;
        var corrected = 0;
        var requeued = 0;
        foreach (var song in ownedSongs)
        {
            if (!songToTrack.TryGetValue(song.Id, out var track))
                continue;

            var changes = song.ApplyCanonicalCorrection(
                canonical.DisplayTitle, canonical.DisplayArtist, canonical.Year, track.TrackNumber, track.DiscNumber);

            if (changes.Count > 0)
            {
                corrected++;
                foreach (var (field, oldValue, newValue) in changes)
                {
                    db.SongMetadataChanges.Add(new SongMetadataChange
                    {
                        SongId = song.Id,
                        FieldName = field,
                        OldValue = oldValue,
                        NewValue = newValue,
                        Source = "canonical-album",
                        Confidence = 1.0,
                        CreatedAtUtc = now,
                        AppliedAtUtc = now,
                    });
                }
            }

            // Re-tag the whole matched album in one build pass so the reconciler re-runs over the now
            // unified folder. RequeueForRetag sets PreviousDestinationPath (the force-rebuild signal);
            // because it also moves the row off Done, RebuildOnMetadataChangeInterceptor skips it, so
            // there's no double reset even though we changed tag-relevant columns above.
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        var unmatched = ownedSongs.Count - songToTrack.Count;
        var safeArtist = artist.Replace("\r", "").Replace("\n", "");
        var safeAlbum = album.Replace("\r", "").Replace("\n", "");
        logger.LogInformation(
            "Canonical consolidation for {Artist} / {Album}: {Matched}/{Owned} matched, {Corrected} corrected, {Requeued} re-queued, {Unmatched} unmatched (kept per-song).",
            safeArtist, safeAlbum, songToTrack.Count, ownedSongs.Count, corrected, requeued, unmatched);

        return new CanonicalConsolidationResult(true, songToTrack.Count, corrected, requeued, unmatched);
    }
}
