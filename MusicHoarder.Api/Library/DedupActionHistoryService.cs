using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>One dedup action (a merge/split/heal invocation), reconstructed from its audit batch.</summary>
public sealed record DedupActionSummary(
    string Source,
    DateTime CreatedAtUtc,
    /// <summary>Stable batch id for revert calls (the batch timestamp's ticks — every change of one
    /// invocation shares a single stamp).</summary>
    long BatchTicks,
    int SongCount,
    int ChangeCount,
    /// <summary>Human labels like "AlbumArtist → 'Kanye West' (348 songs)".</summary>
    IReadOnlyList<string> Highlights,
    /// <summary>True when every change in the batch has been reverted.</summary>
    bool Reverted,
    /// <summary>False for heal batches (the idle heal would immediately re-apply them) and for
    /// fully-reverted batches.</summary>
    bool Revertible);

public sealed record DedupActionRevertResult(int SongsReverted, int ChangesReverted, int SongsRequeued, int AliasesRemoved);

public interface IDedupActionHistory
{
    Task<IReadOnlyList<DedupActionSummary>> ListAsync(int take = 20, CancellationToken ct = default);

    /// <summary>Reverts every not-yet-reverted change of one action batch: fields restored to their
    /// old values, built rows re-queued for re-tag, and (for artist merges) the alias rows removed
    /// so heals/enrichment stop re-applying the merged spelling.</summary>
    Task<DedupActionRevertResult> RevertAsync(Guid ownerUserId, string source, long batchTicks, CancellationToken ct = default);
}

/// <summary>
/// User-facing history of dedup actions. There is no dedicated action table — every merge/split/heal
/// already writes its field changes to <see cref="SongMetadataChange"/> with one shared timestamp per
/// invocation, so (Source, CreatedAtUtc) reconstructs the batch. That also means actions performed
/// before this feature shipped are visible and revertible.
/// </summary>
public sealed class DedupActionHistoryService(
    MusicHoarderDbContext db,
    ILogger<DedupActionHistoryService> logger) : IDedupActionHistory
{
    /// <summary>Sources this history covers; order is irrelevant.</summary>
    public static readonly string[] DedupSources =
        ["artist-merge", "album-merge", "artist-credit-split", "album-identity-heal"];

    /// <summary>Heals are convergence passes, not user decisions: reverting one would only make the
    /// next idle heal re-apply it (that ping-pong is exactly the oscillation class this codebase has
    /// been bitten by before), so the history lists them for transparency but refuses to revert.</summary>
    public const string NonRevertibleSource = "album-identity-heal";

    // Reads go through the per-user query filter on SongMetadataChanges (mirrors Songs), so an
    // endpoint-scoped context only ever sees the caller's own history.
    public async Task<IReadOnlyList<DedupActionSummary>> ListAsync(int take = 20, CancellationToken ct = default)
    {
        // Recent slice only — batches are reconstructed client-side (grouping by a computed key
        // doesn't translate on the in-memory provider, and the slice is small anyway).
        var changes = await db.SongMetadataChanges
            .AsNoTracking()
            .Where(c => DedupSources.Contains(c.Source) && c.AppliedAtUtc != null)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(5000)
            .Select(c => new { c.Source, c.CreatedAtUtc, c.SongId, c.FieldName, c.NewValue, c.RevertedAtUtc })
            .ToListAsync(ct);

        return changes
            .GroupBy(c => (c.Source, c.CreatedAtUtc))
            .OrderByDescending(g => g.Key.CreatedAtUtc)
            .Take(take)
            .Select(g =>
            {
                var reverted = g.All(c => c.RevertedAtUtc != null);
                var highlights = g
                    .GroupBy(c => (c.FieldName, c.NewValue))
                    .OrderByDescending(x => x.Count())
                    .Take(3)
                    .Select(x => $"{x.Key.FieldName} → “{x.Key.NewValue}” ({x.Count()} song{(x.Count() == 1 ? "" : "s")})")
                    .ToList();
                return new DedupActionSummary(
                    g.Key.Source,
                    g.Key.CreatedAtUtc,
                    g.Key.CreatedAtUtc.Ticks,
                    g.Select(c => c.SongId).Distinct().Count(),
                    g.Count(),
                    highlights,
                    reverted,
                    Revertible: !reverted && g.Key.Source != NonRevertibleSource);
            })
            .ToList();
    }

    public async Task<DedupActionRevertResult> RevertAsync(
        Guid ownerUserId, string source, long batchTicks, CancellationToken ct = default)
    {
        if (!DedupSources.Contains(source))
            throw new ArgumentException($"Unknown dedup action source '{source}'.", nameof(source));
        if (source == NonRevertibleSource)
            throw new InvalidOperationException(
                "Identity-heal batches can't be reverted — the automatic heal pass would immediately re-apply them.");

        var stamp = new DateTime(batchTicks, DateTimeKind.Utc);
        var changes = await db.SongMetadataChanges
            .Where(c => c.Source == source
                && c.CreatedAtUtc == stamp
                && c.AppliedAtUtc != null
                && c.RevertedAtUtc == null)
            .Include(c => c.Song)
            .ToListAsync(ct);

        if (changes.Count == 0)
            return new DedupActionRevertResult(0, 0, 0, 0);

        var now = DateTime.UtcNow;
        var requeued = 0;

        foreach (var songChanges in changes.GroupBy(c => c.SongId))
        {
            var song = songChanges.First().Song;

            // Restore in reverse application order so multi-change fields land on the oldest value.
            foreach (var change in songChanges.OrderByDescending(c => c.Id))
            {
                SongFieldReverter.Apply(song, change.FieldName, change.OldValue);
                change.RevertedAtUtc = now;
            }

            // Re-tag the built file back to the restored values (see ArtistCreditHealer for the
            // RequeueForRetag semantics). Rows the original action already re-queued are mid-build
            // and pick the restored values up on their normal turn.
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        // Reverting a merge must also retire its aliases, otherwise the split-heal and enrichment
        // keep mapping the variants back onto the canonical spelling and the revert un-does itself.
        var aliasesRemoved = 0;
        if (source is "artist-merge")
        {
            var canonicals = changes
                .Select(c => c.NewValue)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var aliases = await db.ArtistAliases
                .IgnoreQueryFilters()
                .Where(a => a.OwnerUserId == ownerUserId
                    && (a.CreatedAtUtc == stamp || canonicals.Contains(a.CanonicalName)))
                .ToListAsync(ct);
            db.ArtistAliases.RemoveRange(aliases);
            aliasesRemoved = aliases.Count;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Reverted dedup action {Source}@{Stamp:o}: {Changes} changes on {Songs} songs, {Requeued} re-queued, {Aliases} aliases removed",
            source, stamp, changes.Count, changes.Select(c => c.SongId).Distinct().Count(), requeued, aliasesRemoved);

        return new DedupActionRevertResult(
            changes.Select(c => c.SongId).Distinct().Count(), changes.Count, requeued, aliasesRemoved);
    }
}

/// <summary>Field-name → setter map for restoring an audited old value onto a song row. Superset of
/// the fields the dedup actions write; shared with the per-change revert endpoint.</summary>
public static class SongFieldReverter
{
    public static void Apply(SongMetadata song, string field, string? value)
    {
        switch (field)
        {
            case nameof(SongMetadata.Artist): song.Artist = value; break;
            case nameof(SongMetadata.AlbumArtist): song.AlbumArtist = value; break;
            case nameof(SongMetadata.Title): song.Title = value; break;
            case nameof(SongMetadata.Album): song.Album = value; break;
            case nameof(SongMetadata.Artists): song.Artists = value; break;
            case nameof(SongMetadata.ArtistMusicBrainzIds): song.ArtistMusicBrainzIds = value; break;
            case nameof(SongMetadata.AlbumArtistMusicBrainzId): song.AlbumArtistMusicBrainzId = value; break;
            case nameof(SongMetadata.Year): song.Year = int.TryParse(value, out var y) ? y : null; break;
            case nameof(SongMetadata.TrackNumber): song.TrackNumber = int.TryParse(value, out var t) ? t : null; break;
            case nameof(SongMetadata.DiscNumber): song.DiscNumber = int.TryParse(value, out var d) ? d : null; break;
        }
    }
}
