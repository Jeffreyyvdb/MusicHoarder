using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
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
    ILogger<AlbumSplitHealer> logger) : IAlbumSplitHealer
{
    private const string ChangeSource = "album-identity-heal";

    public async Task<IReadOnlyList<AlbumSplitGroup>> DetectAsync(CancellationToken ct = default)
    {
        // Untracked copies: ApplyIdentityCorrection below mutates them to count would-be changes
        // with the exact heal logic, but nothing is saved.
        var songs = await QueryEligible().AsNoTracking().ToListAsync(ct);

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

            var identity = reconciler.Reconcile(members);
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

        var now = DateTime.UtcNow;
        var groupsHealed = 0;
        var corrected = 0;
        var requeued = 0;

        foreach (var group in GroupByLogicalAlbum(songs))
        {
            var members = group.ToList();
            var identity = reconciler.Reconcile(members);
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
        .Where(s => s.OwnerUserId != WellKnownUsers.DemoId)
        .Where(s => !s.IsDuplicate && !s.IsUnreleased)
        .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched);

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
