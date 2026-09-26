using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
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
    /// changes nothing. Corrections are reversible and never bump EnrichedAtUtc. First, it gives back
    /// album artists it once copied from a canonical album that turned out to be a different album.
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
        // Untracked copies: the restore and ApplyIdentityCorrection below mutate them to count would-be
        // changes with the exact heal logic, but nothing is saved.
        var songs = await QueryEligible().AsNoTracking().ToListAsync(ct);
        var canonicalAlbums = await LoadCanonicalAlbumsAsync(ct);
        var artistAliases = await ArtistAliasMap.LoadAsync(db, ct);
        var (restored, _) = await RestoreForeignAlbumArtistsAsync(songs, canonicalAlbums, DateTime.UtcNow, persist: false, ct);

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

            var identity = OverlayCanonicalArtist(reconciler.Reconcile(members), group.Key, members, canonicalAlbums);
            identity = await FreezeOscillatingAlbumArtistAsync(identity, members, ct);
            identity = ApplyArtistAlias(identity, group.Key.OwnerUserId, artistAliases);
            var needingCorrection = members.Count(s => s.ApplyIdentityCorrection(identity).Count > 0 || restored.Contains(s.Id));
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
        var canonicalAlbums = await LoadCanonicalAlbumsAsync(ct);
        var artistAliases = await ArtistAliasMap.LoadAsync(db, ct);

        var now = DateTime.UtcNow;
        var groupsHealed = 0;
        var requeued = 0;

        // Before grouping: AlbumGroupKey reads AlbumArtist, so a restored song must join its real
        // album's group, not the wrong album's.
        var (restored, restoredRequeued) = await RestoreForeignAlbumArtistsAsync(songs, canonicalAlbums, now, persist: true, ct);
        var correctedIds = new HashSet<int>(restored);
        requeued += restoredRequeued;

        foreach (var group in GroupByLogicalAlbum(songs))
        {
            var members = group.ToList();
            var identity = OverlayCanonicalArtist(reconciler.Reconcile(members), group.Key, members, canonicalAlbums);
            identity = await FreezeOscillatingAlbumArtistAsync(identity, members, ct);
            identity = ApplyArtistAlias(identity, group.Key.OwnerUserId, artistAliases);
            var groupTouched = members.Any(m => restored.Contains(m.Id));

            foreach (var song in members)
            {
                var changes = song.ApplyIdentityCorrection(identity);
                if (changes.Count == 0)
                {
                    continue;
                }

                groupTouched = true;
                correctedIds.Add(song.Id);
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

        if (correctedIds.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new AlbumSplitHealResult(groupsHealed, correctedIds.Count, requeued);
    }

    // Anti-oscillation guard. AlbumGroupKey.For derives its artist key from AlbumArtist — the very
    // field this healer rewrites — so an album-artist correction can move a song into a different
    // logical group whose canonical overlay elects the *other* spelling and flips it back on the next
    // pass. The key's lead-artist fold closes that for a collaborator-suffix rewrite ("Marvin Gaye" ⇄
    // "Marvin Gaye & Tammi Terrell"), which is now a single group; a rewrite that changes the LEAD
    // name ("Lauryn Hill" ⇄ "Ms. Lauryn Hill") still crosses groups, so this guard stays.
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
        // IgnoreQueryFilters like every other query here: the hosted service's scope carries a
        // request-less current-user accessor, so the tenancy filter is on with an empty user id and
        // would hide the whole heal log. The guard then never tripped on prod and one album flipped
        // ~29,000 times. The member ids already pin this to one owner's rows.
        // Only spellings of the elected artist are contenders. The log also holds names a canonical
        // album of a different album once wrote ("Kanye West" → "Fat Joe" → "Chiqito"), and letting
        // those compete would freeze an album onto a stranger's name — "2 Chainz" sorts before
        // "Kanye West".
        var healedAlbumArtists = (await db.SongMetadataChanges
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => memberIds.Contains(c.SongId)
                    && c.Source == ChangeSource
                    && c.FieldName == nameof(SongMetadata.AlbumArtist)
                    && c.NewValue != null)
                .Select(c => c.NewValue!)
                .Distinct()
                .ToListAsync(ct))
            .Where(v => CanonicalAlbumMatch.SameArtist(v, identity.AlbumArtist, options.Value.IdentityArtistThreshold))
            .ToList();

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

    // A user's artist merge outranks every other album-artist source — the canonical overlay, the
    // majority vote, even the oscillation freeze. Without this final mapping the idle heal would
    // rewrite AlbumArtist back to whatever spelling the canonical album or member majority carries,
    // silently un-doing the merge on its next pass (and because merge writes aren't
    // "album-identity-heal"-sourced, the freeze breaker would never trip on the ping-pong). The
    // alias map is deterministic and stable, so this stays a fixed point across passes.
    private static AlbumIdentity ApplyArtistAlias(
        AlbumIdentity identity, Guid ownerUserId, ArtistAliasMap aliases)
    {
        if (aliases.IsEmpty || string.IsNullOrWhiteSpace(identity.AlbumArtist))
            return identity;

        var canonical = aliases.ResolveName(ownerUserId, identity.AlbumArtist);
        return canonical is null ? identity : identity with { AlbumArtist = canonical };
    }

    // Mirrors the builder's reconciliation predicate (LibraryBuilderService.BuildLogicalAlbumIdentityMapAsync):
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

    // One canonical album as the heal reads it: its album artist, plus what it takes to tell whether it
    // is the album its key names (CanonicalAlbumMatch).
    private sealed record CanonicalRow(
        string ArtistKey,
        string AlbumKey,
        string? DisplayTitle,
        string DisplayArtist,
        bool IsFetched,
        IReadOnlyList<string?> TrackTitles);

    private sealed class CanonicalAlbums
    {
        public CanonicalAlbums(IReadOnlyList<CanonicalRow> rows)
        {
            All = rows;
            var fetched = new Dictionary<(string, string), CanonicalRow>();
            foreach (var row in rows.Where(r => r.IsFetched))
                fetched[(row.ArtistKey, row.AlbumKey)] = row;
            Fetched = fetched;
        }

        public static CanonicalAlbums None { get; } = new([]);

        /// <summary>Every row with an album artist, retired ones included — for recognizing old writes.</summary>
        public IReadOnlyList<CanonicalRow> All { get; }

        public IReadOnlyDictionary<(string ArtistKey, string AlbumKey), CanonicalRow> Fetched { get; }
    }

    // The authoritative album-artist per album, from the multi-provider album-enrichment pipeline
    // (CanonicalAlbumFetchService). Keyed exactly as CanonicalAlbum is — NormalizeForSearch(artist)
    // + NormalizeForSearch(album), with NO AlbumGroupKey "|qualifier" suffix on the album half — so a
    // group whose member tags drift from the canonical spelling (e.g. one provider says "Lauryn Hill",
    // another "Ms. Lauryn Hill") can be pulled onto the one canonical name. Empty when the canonical
    // pipeline hasn't fetched anything yet → the heal falls back to the majority-of-tags vote.
    private async Task<CanonicalAlbums> LoadCanonicalAlbumsAsync(CancellationToken ct)
    {
        if (!options.Value.EnableCanonicalDrivenBuild)
            return CanonicalAlbums.None;

        var rows = await db.CanonicalAlbums
            .AsNoTracking()
            .Where(a => a.DisplayArtist != null)
            .Select(a => new { a.Id, a.ArtistKey, a.AlbumKey, a.DisplayTitle, a.DisplayArtist, a.Status })
            .ToListAsync(ct);
        // Only uncontested tracks: rewriting an album artist asks more of a row than keeping it does.
        // The reconciler clusters providers by album title alone, so MusicBrainz's listing of Kanye's
        // "So Help Me God" and 2 Chainz's "So Help Me God!" from Deezer and Apple became one row, named
        // for 2 Chainz, whose extra slots held Kanye's songs. Only MusicBrainz backed those slots — what
        // IsContested records — and counting them let the owner's Kanye leak vouch for 2 Chainz.
        // Contested is blunt (providers that merely format titles differently contest every slot), so
        // CanonicalAlbumFetchService does not use it to decide whether a row is kept.
        var trackTitles = (await db.CanonicalAlbumTracks
                .AsNoTracking()
                .Where(t => !t.IsContested)
                .Select(t => new { t.CanonicalAlbumId, t.Title })
                .ToListAsync(ct))
            .ToLookup(t => t.CanonicalAlbumId, t => t.Title);

        return new CanonicalAlbums(rows
            .Select(r => new CanonicalRow(
                r.ArtistKey, r.AlbumKey, r.DisplayTitle, r.DisplayArtist!,
                r.Status == CanonicalAlbumStatus.Fetched, trackTitles[r.Id].ToList()))
            .ToList());
    }

    // What a canonical tracklist is checked against. Album-fill downloads are left out: they exist
    // because of a canonical album, so they cannot vouch for one (see CanonicalAlbumMatch).
    private static List<string?> OwnedTitles(IEnumerable<SongMetadata> songs) =>
        songs
            .Where(s => s.AcquisitionIntent != SongAcquisitionIntent.AlbumFill)
            .Select(s => s.Title)
            .ToList();

    private bool IsThisAlbum(CanonicalRow row, string? album, string? albumArtist, IEnumerable<string?> ownedTitles) =>
        CanonicalAlbumMatch.IsSameAlbum(
            album, albumArtist, ownedTitles, row.DisplayTitle, row.DisplayArtist, row.TrackTitles,
            options.Value.IdentityTitleThreshold, options.Value.IdentityArtistThreshold);

    // Overlays the reconciled identity's album-artist with the canonical album's authoritative
    // DisplayArtist when one exists for this group and it differs in the folder-affecting (normalized)
    // form. The reconciler already converges album/year within a group; the only axis the build-time
    // folder vote can't bridge is the album-artist STRING (different providers canonicalize the same
    // artist differently), which routes one album into two folders. Driving that one field from the
    // canonical pipeline makes every spelling-variant group converge on the same name without any
    // risky cross-artist merge. Normalized-equality guard avoids cosmetic case-only churn.
    //
    // Only a row naming this album by this artist counts: its title must resemble the album and its
    // artist the elected one (CanonicalAlbumMatch). The overlay used to take any row under the key —
    // one fetched for "CHIRAQ" described a cumbia record, and handed Kanye West's songs to the band.
    // A cross-artist name is refused even from a row that shares tracks with the album: that exception
    // is for showing a featured artist's song on someone else's tracklist, and a tribute or karaoke
    // album shares every title.
    //
    // Lookup is by the members' own album-artist spellings, NOT by groupKey.ArtistKey: the group key
    // is folded to the credit's lead artist (so a collaborator suffix can't move a song between
    // groups), while CanonicalAlbum rows are keyed by the full NormalizeForSearch of the artist the
    // fetch ran for. Trying each distinct member spelling in ordinal order finds the row under either
    // spelling and always picks the SAME one when an album has a row per spelling — the cross-pointing
    // pair that used to swap the two halves of an album on every pass.
    private AlbumIdentity OverlayCanonicalArtist(
        AlbumIdentity identity,
        AlbumGroupKey groupKey,
        IReadOnlyList<SongMetadata> members,
        CanonicalAlbums canonicalAlbums)
    {
        if (canonicalAlbums.Fetched.Count == 0)
            return identity;

        var albumKey = TitleNormalizer.NormalizeForSearch(members[0].Album);
        if (albumKey.Length == 0)
            return identity;

        var artistKeys = members
            .Select(m => TitleNormalizer.NormalizeForSearch(m.AlbumArtist))
            .Append(groupKey.ArtistKey)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        string? displayArtist = null;
        foreach (var artistKey in artistKeys)
        {
            if (canonicalAlbums.Fetched.TryGetValue((artistKey, albumKey), out var row)
                && CanonicalAlbumMatch.TitleMatches(members[0].Album, row.DisplayTitle, options.Value.IdentityTitleThreshold)
                && CanonicalAlbumMatch.SameArtist(identity.AlbumArtist, row.DisplayArtist, options.Value.IdentityArtistThreshold))
            {
                displayArtist = row.DisplayArtist;
                break;
            }
        }

        if (displayArtist is null)
            return identity;

        if (TitleNormalizer.NormalizeForSearch(displayArtist)
            == TitleNormalizer.NormalizeForSearch(identity.AlbumArtist))
            return identity;

        return identity with { AlbumArtist = displayArtist };
    }

    // Undoes album artists this heal once copied from a canonical album that was a different album.
    // Before rows had to prove themselves, the overlay took whatever a row said: Kanye West's unreleased
    // "CHIRAQ" went to Fat Joe, then Chiqito, then Rigo Dominguez — each write moved the songs under a
    // new key whose search found the next stranger — and album completion downloaded the last one's
    // album. The overlay no longer does that, but the majority vote keeps whatever the members carry,
    // so the damage does not undo itself.
    //
    // A song is restored only when all three hold, so a name the heal elected for a good reason stays:
    //  - its album artist is what a row for its album title that is NOT that album named, and no row
    //    that IS that album, fetched under another artist's name, names it (a compilation's real row,
    //    fetched for one contributor, vouches for "Various Artists");
    //  - its own credits (Artist, Artists) don't name it — a Kanye song filed under Kanye is fine;
    //  - this heal wrote the current value (it is the newest album-artist entry in the heal log).
    // It gets back the newest value the log shows it held before a foreign write, else its own artist
    // credit. Rows are checked against every song carrying the album title rather than one group's:
    // the damaged songs have already moved under the stranger's key.
    private async Task<(IReadOnlySet<int> SongIds, int Requeued)> RestoreForeignAlbumArtistsAsync(
        IReadOnlyList<SongMetadata> songs, CanonicalAlbums canonicalAlbums, DateTime now, bool persist, CancellationToken ct)
    {
        var restored = new HashSet<int>();
        if (canonicalAlbums.All.Count == 0)
            return (restored, 0);

        var artistThreshold = options.Value.IdentityArtistThreshold;
        var titlesByAlbum = songs
            .GroupBy(s => TitleNormalizer.NormalizeForSearch(s.Album))
            .ToDictionary(g => g.Key, OwnedTitles);

        var named = new Dictionary<string, List<string>>();
        var foreign = new Dictionary<string, HashSet<string>>();
        foreach (var row in canonicalAlbums.All)
        {
            var owned = titlesByAlbum.GetValueOrDefault(row.AlbumKey) ?? [];
            if (IsThisAlbum(row, row.AlbumKey, row.ArtistKey, owned))
            {
                // A row fetched under its own artist's name says nothing about songs that came to that
                // name from elsewhere: once the heal had filed Kanye's "So Help Me God" leak under
                // 2 Chainz, the fetch for the 2 Chainz key found 2 Chainz's real album of that name.
                if (CanonicalAlbumMatch.SameArtist(row.ArtistKey, row.DisplayArtist, artistThreshold))
                    continue;
                if (!named.TryGetValue(row.AlbumKey, out var names)) named[row.AlbumKey] = names = [];
                names.Add(row.DisplayArtist);
            }
            else
            {
                if (!foreign.TryGetValue(row.AlbumKey, out var names)) foreign[row.AlbumKey] = names = [];
                names.Add(TitleNormalizer.NormalizeForSearch(row.DisplayArtist));
            }
        }

        bool IsForeign(string? album, string albumArtist)
        {
            var albumKey = TitleNormalizer.NormalizeForSearch(album);
            return foreign.TryGetValue(albumKey, out var strangers)
                && strangers.Contains(TitleNormalizer.NormalizeForSearch(albumArtist))
                && !(named.TryGetValue(albumKey, out var names)
                    && names.Any(n => CanonicalAlbumMatch.SameArtist(n, albumArtist, artistThreshold)));
        }

        bool IsCredited(SongMetadata song, string name) =>
            MultiValue.Split(song.Artists)
                .Append(song.Artist)
                .Any(credit => !string.IsNullOrWhiteSpace(credit)
                    && CanonicalAlbumMatch.SameArtist(credit, name, artistThreshold));

        var suspects = songs
            .Where(s => !string.IsNullOrWhiteSpace(s.AlbumArtist)
                && IsForeign(s.Album, s.AlbumArtist!)
                && !IsCredited(s, s.AlbumArtist!))
            .ToList();
        if (suspects.Count == 0)
            return (restored, 0);

        var suspectIds = suspects.Select(s => s.Id).ToList();
        // IgnoreQueryFilters for the reason the freeze gives: the hosted service's scope would hide the log.
        var chains = (await db.SongMetadataChanges
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => suspectIds.Contains(c.SongId)
                    && c.Source == ChangeSource
                    && c.FieldName == nameof(SongMetadata.AlbumArtist))
                .Select(c => new { c.Id, c.SongId, c.CreatedAtUtc, c.OldValue, c.NewValue })
                .ToListAsync(ct))
            .GroupBy(c => c.SongId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id).ToList());

        var requeued = 0;
        foreach (var song in suspects)
        {
            // Not the heal's doing: enrichment or the owner put it there, and that is theirs to change.
            if (!chains.TryGetValue(song.Id, out var chain)
                || !string.Equals(chain[^1].NewValue, song.AlbumArtist, StringComparison.Ordinal))
                continue;

            var previous = song.AlbumArtist;
            var restoreTo = Enumerable.Reverse(chain)
                .Select(c => c.OldValue)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !IsForeign(song.Album, v))
                ?? song.Artist;
            if (string.IsNullOrWhiteSpace(restoreTo) || string.Equals(restoreTo, previous, StringComparison.Ordinal))
                continue;

            song.CaptureOriginalMetadata();
            song.AlbumArtist = restoreTo;
            restored.Add(song.Id);
            if (!persist)
                continue;

            logger.LogInformation(
                "Restored the album artist of song {SongId} on {Album}: {Previous} came from a canonical album of a different album; back to {RestoredTo}",
                song.Id, song.Album, previous, restoreTo);
            db.SongMetadataChanges.Add(new SongMetadataChange
            {
                SongId = song.Id,
                FieldName = nameof(SongMetadata.AlbumArtist),
                OldValue = previous,
                NewValue = restoreTo,
                Source = ChangeSource,
                Confidence = 1.0,
                CreatedAtUtc = now,
                AppliedAtUtc = now,
            });
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        return (restored, requeued);
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
