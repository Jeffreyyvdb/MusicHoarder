using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>One logical album whose member rows disagree on album identity (a "split" album).</summary>
public sealed record AlbumSplitGroup(
    string ArtistKey,
    string AlbumKey,
    int MemberCount,
    int MembersNeedingCorrection,
    IReadOnlyList<string> DistinctReleaseIds,
    IReadOnlyList<string> DistinctFolders,
    AlbumIdentity ElectedIdentity);

/// <summary>Outcome of a self-heal pass.</summary>
public sealed record AlbumSplitHealResult(int GroupsHealed, int SongsCorrected, int SongsRequeued);

public interface IAlbumSplitHealer
{
    /// <summary>
    /// Dry-run split detection: every logical album (see <see cref="AlbumGroupKey"/>) whose members
    /// disagree on identity fields, with the identity a heal would elect. Read-only.
    /// </summary>
    Task<IReadOnlyList<AlbumSplitGroup>> DetectAsync(CancellationToken ct = default);

    /// <summary>
    /// The split-album safeguard: groups all buildable songs by logical album, elects one
    /// deterministic <see cref="AlbumIdentity"/> per group via <see cref="IAlbumIdentityReconciler"/>,
    /// persists it to every member that disagrees (<see cref="SongMetadata.ApplyIdentityCorrection"/>)
    /// and re-queues already-built members for an in-place re-tag/relocate. Closes the gaps the
    /// folder-keyed build-time reconciliation can't reach: siblings whose divergent album/year/artist
    /// put them in different destination folders, and Done rows whose on-disk tags pre-date the
    /// current election. Idempotent — a second pass over healed members elects the same identity and
    /// changes nothing. Corrections are reversible and never bump EnrichedAtUtc.
    /// </summary>
    Task<AlbumSplitHealResult> HealAsync(CancellationToken ct = default);
}

public sealed class AlbumSplitHealer(
    MusicHoarderDbContext db,
    IAlbumIdentityReconciler reconciler,
    IDestinationPathResolver destinationPathResolver,
    IOptions<MusicEnricherOptions> options,
    ILogger<AlbumSplitHealer> logger) : IAlbumSplitHealer
{
    private const string ChangeSource = "album-identity-heal";

    public async Task<IReadOnlyList<AlbumSplitGroup>> DetectAsync(CancellationToken ct = default)
    {
        // Untracked copies: ApplyIdentityCorrection below mutates them to count would-be changes
        // with the exact heal logic, but nothing is saved.
        var songs = await QueryEligible().AsNoTracking().ToListAsync(ct);
        var canonicalArtists = await LoadCanonicalArtistsAsync(ct);

        var report = new List<AlbumSplitGroup>();
        foreach (var group in GroupByLogicalAlbum(songs))
        {
            var members = group.ToList();
            var distinctFolders = members
                .Select(s => Path.GetDirectoryName(destinationPathResolver.ResolvePath(s)) ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var distinctReleaseIds = members
                .Where(s => !string.IsNullOrWhiteSpace(s.MusicBrainzReleaseId))
                .Select(s => s.MusicBrainzReleaseId!.Trim())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            var identity = OverlayCanonicalArtist(reconciler.Reconcile(members), group.Key, members, canonicalArtists);
            identity = await FreezeOscillatingAlbumArtistAsync(identity, members, ct);
            var needingCorrection = members.Count(s => s.ApplyIdentityCorrection(identity).Count > 0);
            if (needingCorrection == 0)
            {
                continue;
            }

            report.Add(new AlbumSplitGroup(
                group.Key.ArtistKey,
                group.Key.AlbumKey,
                members.Count,
                needingCorrection,
                distinctReleaseIds,
                distinctFolders,
                identity));
        }

        return report
            .OrderBy(g => g.ArtistKey, StringComparer.Ordinal)
            .ThenBy(g => g.AlbumKey, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<AlbumSplitHealResult> HealAsync(CancellationToken ct = default)
    {
        var songs = await QueryEligible().ToListAsync(ct);
        var canonicalArtists = await LoadCanonicalArtistsAsync(ct);

        var now = DateTime.UtcNow;
        var groupsHealed = 0;
        var corrected = 0;
        var requeued = 0;

        foreach (var group in GroupByLogicalAlbum(songs))
        {
            var members = group.ToList();
            var identity = OverlayCanonicalArtist(reconciler.Reconcile(members), group.Key, members, canonicalArtists);
            identity = await FreezeOscillatingAlbumArtistAsync(identity, members, ct);
            var groupTouched = false;

            foreach (var song in members)
            {
                var changes = song.ApplyIdentityCorrection(identity);
                if (changes.Count == 0)
                {
                    continue;
                }

                groupTouched = true;
                corrected++;
                foreach (var (field, oldValue, newValue) in changes)
                {
                    db.SongMetadataChanges.Add(new SongMetadataChange
                    {
                        SongId = song.Id,
                        FieldName = field,
                        OldValue = oldValue,
                        NewValue = newValue,
                        Source = ChangeSource,
                        Confidence = 1.0,
                        CreatedAtUtc = now,
                        AppliedAtUtc = now,
                    });
                }

                // Re-tag/relocate the already-built file so the on-disk tags converge too.
                // RequeueForRetag sets PreviousDestinationPath (the force-rebuild signal) and moves
                // the row off Done, so RebuildOnMetadataChangeInterceptor skips it — no double reset
                // even though tag-relevant columns changed above. Members not yet Done just build
                // with the corrected fields on their normal turn.
                if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
                {
                    song.RequeueForRetag();
                    requeued++;
                }
            }

            if (groupTouched)
            {
                groupsHealed++;
                logger.LogInformation(
                    "Healed split album group {ArtistKey} / {AlbumKey}: {Members} members -> release {ReleaseId}",
                    group.Key.ArtistKey, group.Key.AlbumKey, members.Count, identity.MusicBrainzReleaseId);
            }
        }

        if (groupsHealed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new AlbumSplitHealResult(groupsHealed, corrected, requeued);
    }

    // Anti-oscillation guard. AlbumGroupKey.For derives its artist key from AlbumArtist — the very
    // field this healer rewrites — so an album-artist correction moves a song into a different logical
    // group whose canonical overlay can elect the *other* spelling and flip it back on the next pass.
    // Left unchecked this loops forever (observed on prod: one track's album-artist rewritten ~4,700
    // times, alternating between two spellings, forcing a re-tag each flip). Once a group has been
    // heal-assigned two or more distinct album-artist strings, that axis is contended: pin it to a
    // single deterministic winner (ordinal-lowest of the contended spellings) so the election becomes a
    // true fixed point and the requeue/re-tag churn stops. Every other identity field still converges
    // normally, and a first-time correction (only one heal-assigned spelling on record) is untouched.
    private async Task<AlbumIdentity> FreezeOscillatingAlbumArtistAsync(
        AlbumIdentity identity, IReadOnlyList<SongMetadata> members, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identity.AlbumArtist))
        {
            return identity;
        }

        // Only worth a lookup when the election would actually change some member's album-artist.
        if (members.All(m => string.Equals(m.AlbumArtist, identity.AlbumArtist, StringComparison.Ordinal)))
        {
            return identity;
        }

        var memberIds = members.Select(m => m.Id).ToList();
        var healedAlbumArtists = await db.SongMetadataChanges
            .AsNoTracking()
            .Where(c => memberIds.Contains(c.SongId)
                && c.Source == ChangeSource
                && c.FieldName == nameof(SongMetadata.AlbumArtist)
                && c.NewValue != null)
            .Select(c => c.NewValue!)
            .Distinct()
            .ToListAsync(ct);

        // Fewer than two distinct heal-assigned spellings means no flip has happened yet — a normal
        // first-time correction; let it through.
        if (healedAlbumArtists.Count < 2)
        {
            return identity;
        }

        // Contended axis: freeze to the ordinal-lowest spelling — a stable fixed point the canonical
        // overlay can no longer move. Include the current election so a not-yet-persisted candidate
        // still participates.
        var frozen = healedAlbumArtists
            .Append(identity.AlbumArtist)
            .OrderBy(v => v, StringComparer.Ordinal)
            .First();

        if (!string.Equals(frozen, identity.AlbumArtist, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Album-artist oscillation detected across {Count} contended spellings for {Members} members; "
                + "freezing to '{Frozen}'", healedAlbumArtists.Count, members.Count, frozen);
        }

        // Realign the release-artist mbid to travel with the frozen name (deterministic pick) so that
        // field can't keep flipping and re-trigger requeues on its own.
        var frozenMbid = members
            .Where(m => TitleNormalizer.NormalizeForSearch(m.AlbumArtist)
                    == TitleNormalizer.NormalizeForSearch(frozen)
                && !string.IsNullOrWhiteSpace(m.AlbumArtistMusicBrainzId))
            .Select(m => m.AlbumArtistMusicBrainzId!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .FirstOrDefault();

        return identity with
        {
            AlbumArtist = frozen,
            AlbumArtistMusicBrainzId = frozenMbid ?? identity.AlbumArtistMusicBrainzId,
        };
    }

    // Mirrors the builder's reconciliation predicate (LibraryBuilderService.BuildAlbumIdentityMapAsync):
    // every buildable, non-unreleased song, across users (background work bypasses the per-user filter
    // — AlbumGroupKey carries OwnerUserId so groups never cross users).
    //
    // Demo rows are excluded: the read-only demo library is seeded terminal (Done) with
    // DestinationPath == SourcePath (it streams straight from the read-only mount). Re-queuing one for
    // a re-tag would set PreviousDestinationPath to that source path and the builder would try to delete
    // it — which fails on the read-only mount, and would destroy the source if it were ever writable.
    private IQueryable<SongMetadata> QueryEligible() => db.Songs
        .IgnoreQueryFilters()
        .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
        .ExcludingDemoTenant()
        .Where(s => !s.IsDuplicate && !s.IsUnreleased)
        .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched);

    // The authoritative album-artist per album, from the multi-provider album-enrichment pipeline
    // (CanonicalAlbumFetchService). Keyed exactly as CanonicalAlbum is — NormalizeForSearch(artist)
    // + NormalizeForSearch(album), with NO AlbumGroupKey "|qualifier" suffix on the album half — so a
    // group whose member tags drift from the canonical spelling (e.g. one provider says "Lauryn Hill",
    // another "Ms. Lauryn Hill") can be pulled onto the one canonical name. Empty when the canonical
    // pipeline hasn't fetched anything yet → the heal falls back to the majority-of-tags vote.
    private async Task<IReadOnlyDictionary<(string ArtistKey, string AlbumKey), string>> LoadCanonicalArtistsAsync(
        CancellationToken ct)
    {
        if (!options.Value.EnableCanonicalDrivenBuild)
            return new Dictionary<(string, string), string>();

        var rows = await db.CanonicalAlbums
            .AsNoTracking()
            .Where(a => a.Status == CanonicalAlbumStatus.Fetched && a.DisplayArtist != null)
            .Select(a => new { a.ArtistKey, a.AlbumKey, a.DisplayArtist })
            .ToListAsync(ct);

        var map = new Dictionary<(string, string), string>();
        foreach (var r in rows)
            map[(r.ArtistKey, r.AlbumKey)] = r.DisplayArtist!;
        return map;
    }

    // Overlays the reconciled identity's album-artist with the canonical album's authoritative
    // DisplayArtist when one exists for this group and it differs in the folder-affecting (normalized)
    // form. The reconciler already converges album/year within a group; the only axis the build-time
    // folder vote can't bridge is the album-artist STRING (different providers canonicalize the same
    // artist differently), which routes one album into two folders. Driving that one field from the
    // canonical pipeline makes every spelling-variant group converge on the same name without any
    // risky cross-artist merge. Normalized-equality guard avoids cosmetic case-only churn.
    private AlbumIdentity OverlayCanonicalArtist(
        AlbumIdentity identity,
        AlbumGroupKey groupKey,
        IReadOnlyList<SongMetadata> members,
        IReadOnlyDictionary<(string ArtistKey, string AlbumKey), string> canonicalArtists)
    {
        if (canonicalArtists.Count == 0)
            return identity;

        var albumKey = TitleNormalizer.NormalizeForSearch(members[0].Album);
        if (albumKey.Length == 0)
            return identity;

        if (!canonicalArtists.TryGetValue((groupKey.ArtistKey, albumKey), out var displayArtist))
            return identity;

        if (TitleNormalizer.NormalizeForSearch(displayArtist)
            == TitleNormalizer.NormalizeForSearch(identity.AlbumArtist))
            return identity;

        return identity with { AlbumArtist = displayArtist };
    }

    // In-memory grouping (the key is computed C#, and the in-memory EF provider used in tests can't
    // translate GroupBy anyway). Single-member groups can't disagree with themselves — skip them.
    private static IEnumerable<IGrouping<AlbumGroupKey, SongMetadata>> GroupByLogicalAlbum(
        IEnumerable<SongMetadata> songs)
        => songs
            .Select(s => (Key: AlbumGroupKey.For(s), Song: s))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, x => x.Song)
            .Where(g => g.Count() > 1);
}
