using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>A matched song missing its discrete artist credit, with the credit a heal would apply.</summary>
public sealed record ArtistCreditGap(
    int SongId,
    string? Artist,
    string? Title,
    EnrichmentProvider DonorProvider,
    string Artists,
    string? ArtistMusicBrainzIds);

/// <summary>Outcome of a self-heal pass.</summary>
public sealed record ArtistCreditHealResult(int SongsHealed, int SongsRequeued);

public interface IArtistCreditHealer
{
    /// <summary>Dry-run detection: every healable song with the credit that would be applied. Read-only.</summary>
    Task<IReadOnlyList<ArtistCreditGap>> DetectAsync(CancellationToken ct = default);

    /// <summary>
    /// Backfills <see cref="SongMetadata.Artists"/> (and aligned <see cref="SongMetadata.ArtistMusicBrainzIds"/>)
    /// for matched songs that predate discrete-artist enrichment, from the matched MusicBrainz/Spotify
    /// attempt already stored in <see cref="SongProviderAttempt.MatchedDataJson"/> — no provider calls.
    /// Without the discrete list the tag writer can't emit per-artist ARTISTS frames, so the combined
    /// display credit becomes one merged "artist" in Navidrome. Already-built rows are re-queued for an
    /// in-place re-tag. Idempotent — healed rows leave the eligibility predicate (Artists is set), so a
    /// second pass finds nothing; no marker columns. Never bumps EnrichedAtUtc (no grade staleness).
    /// </summary>
    Task<ArtistCreditHealResult> HealAsync(CancellationToken ct = default);
}

public sealed class ArtistCreditHealer(
    MusicHoarderDbContext db,
    ILogger<ArtistCreditHealer> logger) : IArtistCreditHealer
{
    private const string ChangeSource = "artist-credit-heal";

    public async Task<IReadOnlyList<ArtistCreditGap>> DetectAsync(CancellationToken ct = default)
    {
        var songs = await QueryEligible().AsNoTracking().ToListAsync(ct);

        var report = new List<ArtistCreditGap>();
        foreach (var song in songs)
        {
            var donor = PickDonor(song);
            if (donor is null)
                continue;

            report.Add(new ArtistCreditGap(
                song.Id, song.Artist, song.Title,
                donor.Value.Provider, donor.Value.Artists, donor.Value.ArtistMusicBrainzIds));
        }

        return report.OrderBy(g => g.SongId).ToList();
    }

    public async Task<ArtistCreditHealResult> HealAsync(CancellationToken ct = default)
    {
        var songs = await QueryEligible().ToListAsync(ct);

        var now = DateTime.UtcNow;
        var healed = 0;
        var requeued = 0;

        foreach (var song in songs)
        {
            var donor = PickDonor(song);
            if (donor is null)
                continue;

            song.CaptureOriginalMetadata();

            db.SongMetadataChanges.Add(new SongMetadataChange
            {
                SongId = song.Id,
                FieldName = nameof(SongMetadata.Artists),
                OldValue = song.Artists,
                NewValue = donor.Value.Artists,
                Source = ChangeSource,
                Confidence = 1.0,
                CreatedAtUtc = now,
                AppliedAtUtc = now,
            });
            song.Artists = donor.Value.Artists;

            if (donor.Value.ArtistMusicBrainzIds is not null && string.IsNullOrWhiteSpace(song.ArtistMusicBrainzIds))
            {
                db.SongMetadataChanges.Add(new SongMetadataChange
                {
                    SongId = song.Id,
                    FieldName = nameof(SongMetadata.ArtistMusicBrainzIds),
                    OldValue = song.ArtistMusicBrainzIds,
                    NewValue = donor.Value.ArtistMusicBrainzIds,
                    Source = ChangeSource,
                    Confidence = 1.0,
                    CreatedAtUtc = now,
                    AppliedAtUtc = now,
                });
                song.ArtistMusicBrainzIds = donor.Value.ArtistMusicBrainzIds;
            }

            healed++;

            // Re-tag the already-built file so the on-disk ARTISTS frames converge too.
            // RequeueForRetag sets PreviousDestinationPath (the force-rebuild signal — FLAC re-tags
            // are size-identical and would otherwise be skipped) and moves the row off Done, so
            // RebuildOnMetadataChangeInterceptor skips it — no double reset even though tag-relevant
            // columns changed above. Rows not yet Done just build with the credit on their normal turn.
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        if (healed > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Artist-credit self-heal: {Healed} songs backfilled with discrete artists, {Requeued} re-queued for re-tag",
                healed, requeued);
        }

        return new ArtistCreditHealResult(healed, requeued);
    }

    // Matched songs without a discrete credit that have a stored MB/Spotify matched attempt to heal
    // from. Demo rows are excluded for the same reason as AlbumSplitHealer: the read-only demo
    // library is seeded terminal with DestinationPath == SourcePath, and a re-queue would point the
    // builder's delete at the source mount. Healed rows fail the Artists predicate → idempotent.
    private IQueryable<SongMetadata> QueryEligible() => db.Songs
        .IgnoreQueryFilters()
        .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
        .Where(s => s.OwnerUserId != WellKnownUsers.DemoId)
        .Where(s => !s.IsDuplicate)
        .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched)
        .Where(s => s.Artists == null || s.Artists == "")
        .Where(s => s.ProviderAttempts.Any(a =>
            (a.Provider == EnrichmentProvider.MusicBrainzWeb || a.Provider == EnrichmentProvider.SpotifyAPI)
            && a.Status == ProviderAttemptStatus.Matched
            && a.MatchedDataJson != null))
        .Include(s => s.ProviderAttempts);

    /// <summary>
    /// The stored attempt to heal from: MusicBrainz preferred over Spotify (it carries aligned
    /// artist MBIDs), and only an attempt that matched the SAME recording the song ended up with —
    /// a stale attempt for a different match must never donate its credit.
    /// </summary>
    private static (EnrichmentProvider Provider, string Artists, string? ArtistMusicBrainzIds)? PickDonor(
        SongMetadata song)
    {
        foreach (var provider in (ReadOnlySpan<EnrichmentProvider>)
                 [EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.SpotifyAPI])
        {
            var attempt = song.ProviderAttempts
                .Where(a => a.Provider == provider
                    && a.Status == ProviderAttemptStatus.Matched
                    && a.MatchedDataJson is not null)
                .OrderByDescending(a => a.AttemptedAtUtc)
                .FirstOrDefault();
            if (attempt is null)
                continue;

            var candidate = TryDeserialize(attempt.MatchedDataJson!);
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Artists))
                continue;

            var sameIdentity = provider switch
            {
                EnrichmentProvider.MusicBrainzWeb =>
                    !string.IsNullOrWhiteSpace(song.MusicBrainzId)
                    && string.Equals(candidate.MusicBrainzId, song.MusicBrainzId, StringComparison.OrdinalIgnoreCase),
                EnrichmentProvider.SpotifyAPI =>
                    !string.IsNullOrWhiteSpace(song.SpotifyId)
                    && string.Equals(candidate.SpotifyId, song.SpotifyId, StringComparison.Ordinal),
                _ => false,
            };
            if (!sameIdentity)
                continue;

            // Ids only when positionally aligned with the artist list — a misaligned pair is worse
            // than names alone.
            var artists = MultiValue.Split(candidate.Artists);
            var ids = MultiValue.Split(candidate.ArtistMusicBrainzIds);
            var alignedIds = ids.Length > 0 && ids.Length == artists.Length
                ? candidate.ArtistMusicBrainzIds
                : null;

            return (provider, candidate.Artists!, alignedIds);
        }

        return null;
    }

    private static EnrichmentProviderResult? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EnrichmentProviderResult>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
