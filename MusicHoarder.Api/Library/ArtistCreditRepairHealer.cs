using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// One field a credit repair would write: which repair (its audit <see cref="SongMetadataChange.Source"/>,
/// one of the <c>ArtistCreditRepairHealer.*Source</c> constants), the value the song holds now and the
/// value the repair restores.
/// </summary>
public sealed record ArtistCreditRepair(
    int SongId,
    string? Title,
    string Kind,
    string Field,
    string? From,
    string? To);

/// <summary>Outcome of a repair pass.</summary>
public sealed record ArtistCreditRepairResult(int SongsRepaired, int FieldsRepaired, int SongsRequeued);

public interface IArtistCreditRepairHealer
{
    /// <summary>Dry-run: every field a repair pass would write, with its current and repaired value. Read-only.</summary>
    Task<IReadOnlyList<ArtistCreditRepair>> DetectAsync(CancellationToken ct = default);

    /// <summary>
    /// Repairs artist credits the first artist-dedup release damaged, from state the rows already
    /// carry — no provider calls:
    /// <list type="bullet">
    /// <item><b>Collab album artist</b> (<see cref="ArtistCreditRepairHealer.AlbumArtistSource"/>): an
    /// album artist that is the joined credit of the song's own discrete list ("Hef met Jayh" over
    /// [Hef, Jayh], written by a MusicBrainz mapping that knew only English join phrases while it took
    /// the LEAD's id), or that carries a featuring clause ("Nas (featuring AZ)", the scanner's fallback
    /// before brackets were understood), becomes the lead artist.</item>
    /// <item><b>Merge</b> (<see cref="ArtistCreditRepairHealer.MergeSource"/>): a display credit an
    /// artist merge rewrote to the lead ("Nas (featuring AZ)" → "Nas") gets its old value back.</item>
    /// <item><b>Credit split</b> (<see cref="ArtistCreditRepairHealer.SplitSource"/>): the phantom
    /// "feat." artist a split wrote into Artists is dropped, and a split that landed on a row whose
    /// credit never named those artists (a solo "Kanye West" row) is undone.</item>
    /// <item><b>Alias</b> (<see cref="ArtistCreditRepairHealer.AliasSource"/>): a featuring credit
    /// enrichment wrote as its lead because a merge alias keyed on the lead matched it gets the
    /// provider's own credit back.</item>
    /// </list>
    /// Every write is audited under its repair's source, captures originals first, re-queues Done rows
    /// for an in-place re-tag/relocate and never bumps EnrichedAtUtc. Idempotent without marker
    /// columns — a repaired row leaves its predicate — and a value is repaired away at most once, so a
    /// user's revert, or a writer this heal does not know about, is never fought.
    /// </summary>
    Task<ArtistCreditRepairResult> HealAsync(CancellationToken ct = default);
}

public sealed class ArtistCreditRepairHealer(
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options,
    ILogger<ArtistCreditRepairHealer> logger) : IArtistCreditRepairHealer
{
    public const string AlbumArtistSource = "album-artist-repair";
    public const string MergeSource = "artist-merge-repair";
    public const string SplitSource = "credit-split-repair";
    public const string AliasSource = "artist-alias-repair";

    private static readonly HashSet<string> RepairSources = new(StringComparer.Ordinal)
    {
        AlbumArtistSource, MergeSource, SplitSource, AliasSource,
    };

    // The sources whose rows these repairs read. Spelled out rather than borrowed from
    // ArtistDuplicateService: they name rows already in the database, so they must not move if that
    // service ever renames its own constants.
    private const string ArtistMergeSource = "artist-merge";
    private const string CreditSplitSource = "artist-credit-split";

    // Writers whose AlbumArtist is an ALBUM-level decision: the split-album heal's election (canonical
    // overlay, member majority, oscillation freeze, merge alias), the canonical-album consolidation, a
    // user's artist or album merge. When one of them wrote the collab spelling the per-row repair
    // defers — fighting it is the ping-pong AlbumArtistOscillationTests exists for.
    private static readonly HashSet<string> AlbumLevelSources = new(StringComparer.Ordinal)
    {
        "album-identity-heal", "canonical-album", ArtistMergeSource, "album-merge",
    };

    // Enrichment audits its writes under the winning provider's MatchedBy, which is the provider name.
    private static readonly string[] ProviderSources = Enum.GetNames<EnrichmentProvider>()
        .Where(n => EnrichmentOrchestrator.MapProviderName(n) is not null)
        .ToArray();

    private const string Artist = nameof(SongMetadata.Artist);
    private const string AlbumArtist = nameof(SongMetadata.AlbumArtist);
    private const string Artists = nameof(SongMetadata.Artists);
    private const string ArtistMusicBrainzIds = nameof(SongMetadata.ArtistMusicBrainzIds);
    private const string AlbumArtistMusicBrainzId = nameof(SongMetadata.AlbumArtistMusicBrainzId);

    public async Task<IReadOnlyList<ArtistCreditRepair>> DetectAsync(CancellationToken ct = default)
    {
        // Untracked copies: planning mutates them (a later repair reads what an earlier one restored),
        // but nothing is saved.
        var plans = await PlanAsync(tracked: false, ct);
        return plans
            .SelectMany(p => p.Changes.Select(c =>
                new ArtistCreditRepair(p.Song.Id, p.Song.Title, c.Source, c.Field, c.OldValue, c.NewValue)))
            .ToList();
    }

    public async Task<ArtistCreditRepairResult> HealAsync(CancellationToken ct = default)
    {
        var plans = await PlanAsync(tracked: true, ct);

        var now = DateTime.UtcNow;
        var fields = 0;
        var requeued = 0;
        foreach (var (song, changes) in plans)
        {
            foreach (var change in changes)
            {
                db.SongMetadataChanges.Add(new SongMetadataChange
                {
                    SongId = song.Id,
                    FieldName = change.Field,
                    OldValue = change.OldValue,
                    NewValue = change.NewValue,
                    Source = change.Source,
                    Confidence = 1.0,
                    CreatedAtUtc = now,
                    AppliedAtUtc = now,
                });
                fields++;
            }

            // Re-tag/relocate the built file so the on-disk frames — and, for an album artist, the
            // artist folder — converge too (see ArtistCreditHealer for the RequeueForRetag semantics).
            if (song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                requeued++;
            }
        }

        if (plans.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Artist-credit repair: {Songs} songs repaired ({Fields} fields), {Requeued} re-queued for re-tag",
                plans.Count, fields, requeued);
        }

        return new ArtistCreditRepairResult(plans.Count, fields, requeued);
    }

    private async Task<List<SongPlan>> PlanAsync(bool tracked, CancellationToken ct)
    {
        // Candidates, cheapest first. The merge/split anchors are curation actions — small by
        // construction. The alias anchors are the enrichment writes of an aliased canonical name.
        var anchoredIds = await db.SongMetadataChanges
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.AppliedAtUtc != null && c.RevertedAtUtc == null)
            .Where(c => (c.Source == ArtistMergeSource && c.FieldName == Artist)
                || (c.Source == CreditSplitSource && c.FieldName == Artists))
            .Select(c => c.SongId)
            .Distinct()
            .ToListAsync(ct);

        var aliasRows = await db.ArtistAliases
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(a => new AliasRow(a.OwnerUserId, a.AliasKey, a.CanonicalName, a.CreatedAtUtc))
            .ToListAsync(ct);

        var aliasAnchoredIds = new List<int>();
        if (aliasRows.Count > 0)
        {
            var canonicalNames = aliasRows.Select(a => a.CanonicalName).Distinct(StringComparer.Ordinal).ToList();
            var earliestAlias = aliasRows.Min(a => a.CreatedAtUtc);
            aliasAnchoredIds = await db.SongMetadataChanges
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => c.FieldName == Artist && c.AppliedAtUtc != null && c.RevertedAtUtc == null)
                .Where(c => c.CreatedAtUtc >= earliestAlias && ProviderSources.Contains(c.Source))
                .Where(c => c.NewValue != null && canonicalNames.Contains(c.NewValue))
                .Select(c => c.SongId)
                .Distinct()
                .ToListAsync(ct);
        }

        // Album-artist candidates: a light projection over the library, the name test in memory (it
        // normalizes, which no provider translates) so only the rows it matches are loaded whole. The
        // full rule, guards included, runs on the loaded row.
        var albumArtistIds = (await QueryEligible()
                .AsNoTracking()
                .Where(s => !s.IsManuallyApproved && s.AlbumArtist != null && s.AlbumArtist != "")
                .Select(s => new { s.Id, s.AlbumArtist, s.Artists })
                .ToListAsync(ct))
            .Where(s => HasExplicitFeaturing(s.AlbumArtist!)
                || ArtistCreditNormalizer.LeadOfJoinedCredit(s.AlbumArtist, DiscreteCredit(s.Artists, null).Names) is not null)
            .Select(s => s.Id);

        var ids = anchoredIds.Concat(aliasAnchoredIds).Concat(albumArtistIds).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var songsQuery = QueryEligible().Where(s => ids.Contains(s.Id));
        var songs = tracked
            ? await songsQuery.ToListAsync(ct)
            : await songsQuery.AsNoTracking().ToListAsync(ct);
        if (songs.Count == 0)
            return [];

        var loadedIds = songs.Select(s => s.Id).ToList();
        var history = (await db.SongMetadataChanges
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => loadedIds.Contains(c.SongId)
                    && (c.FieldName == Artist || c.FieldName == AlbumArtist || c.FieldName == Artists))
                .Select(c => new ChangeRow(
                    c.Id, c.SongId, c.FieldName, c.Source, c.OldValue, c.NewValue, c.CreatedAtUtc,
                    c.AppliedAtUtc != null, c.RevertedAtUtc != null))
                .ToListAsync(ct))
            .GroupBy(c => c.SongId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id).ToList());

        var attempts = aliasAnchoredIds.Count == 0
            ? Enumerable.Empty<SongProviderAttempt>().ToLookup(a => a.SongId)
            : (await db.SongProviderAttempts
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(a => aliasAnchoredIds.Contains(a.SongId)
                        && a.Status == ProviderAttemptStatus.Matched
                        && a.MatchedDataJson != null)
                    .ToListAsync(ct))
                .ToLookup(a => a.SongId);

        var context = new PlanContext(
            aliasRows.ToLookup(a => a.OwnerUserId),
            await ArtistAliasMap.LoadAsync(db, ct),
            await LoadCanonicalArtistsAsync(ct),
            attempts);

        var plans = new List<SongPlan>();
        foreach (var song in songs.OrderBy(s => s.Id))
        {
            var songHistory = history.GetValueOrDefault(song.Id) ?? [];
            var changes = new List<PlannedChange>();

            // Display credit first (the split repair reads it to decide whether a split belonged on
            // this row), then the discrete list (the album-artist repair reads the cleaned one).
            if (!TryRestoreMergedCredit(song, songHistory, changes))
                TryRestoreAliasedCredit(song, songHistory, context, changes);
            TryRepairSplitCredit(song, songHistory, changes);
            TryRepairAlbumArtist(song, songHistory, context, changes);

            if (changes.Count > 0)
                plans.Add(new SongPlan(song, changes));
        }

        return plans;
    }

    // (b) An artist merge rewrote every whole-field value whose SEARCH key matched — and the search
    // key strips featuring clauses and brackets, so an unselected "Nas (featuring AZ)" (or a collab
    // "Hef met Jayh" offered as a spelling of Hef) became "Nas", deleting the guest. Restored only
    // while the merge is still the last word on the field and the old value is a multi-artist credit
    // by an unambiguous test (a featuring clause, or the joined form of the row's own discrete list):
    // a merge that folded "Simon and Garfunkel" into "Simon & Garfunkel" was a spelling fix, not this.
    private static bool TryRestoreMergedCredit(SongMetadata song, List<ChangeRow> history, List<PlannedChange> changes)
    {
        var last = LastApplied(history, Artist);
        if (last is null
            || last.Source != ArtistMergeSource
            || !string.Equals(last.NewValue, song.Artist, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(last.OldValue)
            || string.Equals(last.OldValue, last.NewValue, StringComparison.Ordinal)
            || AlreadyRepairedAway(history, Artist, song.Artist)
            || ApprovedSince(song, last.CreatedAtUtc))
            return false;

        if (!IsMultiArtistCredit(last.OldValue, DiscreteCredit(song.Artists, song.ArtistMusicBrainzIds).Names))
            return false;

        Apply(song, Artist, last.OldValue, MergeSource, changes);
        return true;
    }

    // (d) Every merge stores an alias for the canonical's own search key, and enrichment routed the
    // winner's credit through ResolveName — keyed by that same stripping search form — so a matched
    // "Nas feat. AZ" was written as "Nas". Deterministic from stored state: enrichment audits the
    // write (the merger fills a blank or upgrades a differing value, and records it under the winner's
    // MatchedBy), the winner's own un-aliased result is still on its Matched attempt, and the alias
    // row says when it began to apply. Restored only while that write is the last word, the alias
    // existed before it, the provider's credit keys to the alias, and that credit is multi-artist by
    // the same unambiguous test as the merge restore. The restore is exactly what the merger would
    // have written without the alias: the same fill/upgrade decision, applied to the real credit.
    private static void TryRestoreAliasedCredit(
        SongMetadata song, List<ChangeRow> history, PlanContext context, List<PlannedChange> changes)
    {
        var last = LastApplied(history, Artist);
        if (last is null
            || !ProviderSources.Contains(last.Source)
            || !string.Equals(last.NewValue, song.Artist, StringComparison.Ordinal)
            || AlreadyRepairedAway(history, Artist, song.Artist)
            || ApprovedSince(song, last.CreatedAtUtc))
            return;

        var provider = EnrichmentOrchestrator.MapProviderName(last.Source);
        var attempt = context.Attempts[song.Id]
            .Where(a => a.Provider == provider && a.AttemptedAtUtc <= last.CreatedAtUtc)
            .OrderByDescending(a => a.AttemptedAtUtc)
            .FirstOrDefault();
        var candidate = attempt is null ? null : TryDeserialize(attempt.MatchedDataJson!);
        if (candidate is null || !string.Equals(candidate.MatchedBy, last.Source, StringComparison.Ordinal))
            return;

        var credit = candidate.Artist?.Trim();
        if (string.IsNullOrWhiteSpace(credit) || string.Equals(credit, song.Artist, StringComparison.Ordinal))
            return;

        var creditKey = TitleNormalizer.NormalizeForSearch(credit);
        var aliased = context.AliasRows[song.OwnerUserId].Any(a =>
            a.AliasKey == creditKey
            && string.Equals(a.CanonicalName, song.Artist, StringComparison.Ordinal)
            && a.CreatedAtUtc <= last.CreatedAtUtc);
        if (!aliased || !IsMultiArtistCredit(credit, DiscreteCredit(song.Artists, song.ArtistMusicBrainzIds).Names))
            return;

        Apply(song, Artist, credit, AliasSource, changes);
    }

    // (c) Two split defects. The featuring regex's capturing group made Regex.Split return the
    // delimiter itself, so "Kanye West feat. Kid Cudi" wrote [Kanye West; feat.; Kid Cudi]; and rows
    // were matched by SEARCH key, which for a featuring credit is the lead's, so every blank-list solo
    // "Kanye West" row got the guest too. The split never wrote ids (only Artists).
    private static void TryRepairSplitCredit(SongMetadata song, List<ChangeRow> history, List<PlannedChange> changes)
    {
        var split = history.LastOrDefault(c =>
            c.Source == CreditSplitSource && c.FieldName == Artists && c.Applied && !c.Reverted);
        if (split is null
            || string.IsNullOrWhiteSpace(song.Artists)
            || AlreadyRepairedAway(history, Artists, song.Artists)
            || ApprovedSince(song, split.CreatedAtUtc))
            return;

        var segments = MultiValue.Split(song.Artists);
        var (parts, alignedIds) = DiscreteCredit(song.Artists, song.ArtistMusicBrainzIds);

        // Wrong row: the split is still the list's last word and its parts are not the row's credit.
        // Restore the blank it filled (the split only ever wrote into blank lists), so the
        // artist-credit heal can backfill the real list from the matched attempt.
        var untouched = LastApplied(history, Artists) is { } last
            && last.Id == split.Id
            && string.Equals(song.Artists, split.NewValue, StringComparison.Ordinal);
        if (untouched && !ArtistCreditNormalizer.IsJoinedCreditOf(song.Artist, parts))
        {
            Apply(song, Artists, split.OldValue, SplitSource, changes);
            return;
        }

        // Phantom delimiter segments. A "feat." segment is never an artist, so this holds even after
        // a later merge renamed another segment — as long as the split is what introduced it.
        if (parts.Count == segments.Length
            || !MultiValue.Split(split.NewValue).Any(ArtistCreditNormalizer.IsDelimiterToken))
            return;

        Apply(song, Artists, MultiValue.Join(parts), SplitSource, changes);
        if (alignedIds is not null)
            Apply(song, ArtistMusicBrainzIds, MultiValue.Join(alignedIds), SplitSource, changes);
    }

    // (a) The album artist is the lead of the credit. The MusicBrainz mapping took the NAME from the
    // joined credit text — split only on the English join phrases SplitArtists knows — but the ID from
    // the first credit entry, so "Hef met Jayh" / "2Pac + Outlawz" / "50 Cent and Olivia" carried the
    // lead's id, filed a folder of their own and clustered as a spelling of the lead. Recognized from
    // the row's own discrete list (MusicBrainz's split, so no hard-coded joiner that would break
    // "Florence + the Machine"); a value SplitArtists already splits ("Marvin Gaye & Tammi Terrell")
    // never came from that bug and is left to the album-level writers. A featuring clause in an album
    // artist is never right either way — the scanner derived "Nas (featuring AZ)" before brackets
    // were understood.
    //
    // No oscillation: the old spelling can only come back through an album-level writer, and each one
    // is either checked here before the write (the canonical album the split heal overlays, the merge
    // alias it applies last) or deferred to after it (AlbumLevelSources), and a value is repaired away
    // at most once. The member majority cannot elect a collab spelling at all — one SplitArtists does
    // not split keys a different logical album than its lead (AlbumGroupKey folds only what
    // SplitArtists splits). A featuring spelling shares the lead's search key, so the majority can
    // pick it only as the ordinal-lowest spelling of that key on some row this repair left alone
    // ("NAS (feat. X)" sorts before "Nas"); that write is album-level, and deferred to.
    private void TryRepairAlbumArtist(
        SongMetadata song, List<ChangeRow> history, PlanContext context, List<PlannedChange> changes)
    {
        var current = song.AlbumArtist;
        if (song.IsManuallyApproved
            || string.IsNullOrWhiteSpace(current)
            || AlreadyRepairedAway(history, AlbumArtist, current)
            || UserRestored(history, AlbumArtist, current))
            return;

        if (LastApplied(history, AlbumArtist) is { } last
            && AlbumLevelSources.Contains(last.Source)
            && string.Equals(last.NewValue, current, StringComparison.Ordinal))
            return;

        var (names, ids) = DiscreteCredit(song.Artists, song.ArtistMusicBrainzIds);
        string? lead = null;
        string? leadMbid = null;
        if (ArtistCreditNormalizer.LeadOfJoinedCredit(current, names) is { } joinedLead
            // The id is the bug's fingerprint: the lead's (or none). An album artist carrying some
            // other id — a group entity, the release artist — is somebody's deliberate value.
            && (string.IsNullOrWhiteSpace(song.AlbumArtistMusicBrainzId)
                || (ids is not null
                    && string.Equals(song.AlbumArtistMusicBrainzId.Trim(), ids[0], StringComparison.OrdinalIgnoreCase))))
        {
            lead = joinedLead;
            leadMbid = ids?[0];
        }
        else if (HasExplicitFeaturing(current)
            // The row's own list naming the whole value as ONE artist outranks the text.
            && !(names.Count == 1
                && TitleNormalizer.NormalizeArtistKey(names[0]) == TitleNormalizer.NormalizeArtistKey(current)))
        {
            lead = ArtistCreditNormalizer.GetPrimaryArtist(current);
        }

        if (string.IsNullOrWhiteSpace(lead)
            || TitleNormalizer.NormalizeArtistKey(lead) == TitleNormalizer.NormalizeArtistKey(current))
            return;

        // Route the new spelling through the owner's merge aliases, like every other artist-name
        // writer (see ArtistAliasMap). The split heal applies aliases through the same map, so the two
        // cannot disagree; a legacy alias that folds the lead INTO the collab ("Hef" merged into "Hef
        // met Jayh") is ignored by both (ArtistAliasMap.JoinsAsPart), and should a valid alias still
        // map the lead back onto the current spelling, the key check below leaves the row alone.
        var target = context.Aliases.ResolveName(song.OwnerUserId, lead) ?? lead;
        if (TitleNormalizer.NormalizeArtistKey(target) == TitleNormalizer.NormalizeArtistKey(current)
            || CanonicalAlbumPullsBack(song, target, current, context.CanonicalArtists))
            return;

        Apply(song, AlbumArtist, target, AlbumArtistSource, changes);
        if (!string.IsNullOrWhiteSpace(leadMbid)
            && TitleNormalizer.NormalizeForSearch(target) == TitleNormalizer.NormalizeForSearch(lead)
            && !string.Equals(song.AlbumArtistMusicBrainzId, leadMbid, StringComparison.Ordinal))
            Apply(song, AlbumArtistMusicBrainzId, leadMbid, AlbumArtistSource, changes);
    }

    // Mirrors AlbumSplitHealer.OverlayCanonicalArtist for the logical album the repaired row lands in:
    // when that album's canonical row names the collab spelling, the split heal would overlay it right
    // back — the canonical pipeline says the album is the collab's, so leave it.
    private static bool CanonicalAlbumPullsBack(
        SongMetadata song, string target, string current,
        IReadOnlyDictionary<(string ArtistKey, string AlbumKey), string> canonicalArtists)
    {
        if (canonicalArtists.Count == 0)
            return false;

        var albumKey = TitleNormalizer.NormalizeForSearch(song.Album);
        if (albumKey.Length == 0)
            return false;

        var targetKey = TitleNormalizer.NormalizeForSearch(target);
        var currentKey = TitleNormalizer.NormalizeForSearch(current);
        foreach (var artistKey in new[] { targetKey, AlbumGroupKey.ComputeArtistKey(target) }.Distinct(StringComparer.Ordinal))
        {
            if (canonicalArtists.TryGetValue((artistKey, albumKey), out var display)
                && TitleNormalizer.NormalizeForSearch(display) != targetKey
                && TitleNormalizer.NormalizeForSearch(display) == currentKey)
                return true;
        }

        return false;
    }

    // Keyed exactly as AlbumSplitHealer.LoadCanonicalArtistsAsync, behind the same option — the
    // overlay this guards against only runs when canonical-driven builds are on.
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

    /// <summary>
    /// Several artists by a test that cannot mistake one name for two: an explicit featuring clause
    /// (bare or bracketed), or the joined form of the row's own discrete list. Never the "&amp;" / ","
    /// / "+" / "and" joiners on their own — "Simon &amp; Garfunkel" is one artist.
    /// </summary>
    private static bool IsMultiArtistCredit(string credit, IReadOnlyList<string> discreteArtists) =>
        ArtistCreditNormalizer.HasFeaturingDelimiter(credit)
        || ArtistCreditNormalizer.IsJoinedCreditOf(credit, discreteArtists);

    /// <summary>
    /// A featuring clause this heal may act on unattended: feat./ft./featuring, or a bracketed
    /// "(with X)". A bare " with " is also a word inside names ("Dance With the Dead"), and cutting an
    /// album artist there would move a whole album into the wrong artist's folder.
    /// </summary>
    private static bool HasExplicitFeaturing(string credit) => ExplicitFeaturing.IsMatch(credit);

    private static readonly Regex ExplicitFeaturing = new(
        @"\s(?:feat\.?|ft\.?|featuring)\s|[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A user's undo that put this exact value back — of a split, a merge or any other change to the
    // field — is a decision about the row, not damage; re-applying it would silently undo the undo.
    private static bool UserRestored(List<ChangeRow> history, string field, string? current) =>
        history.Any(c => c.FieldName == field
            && c.Reverted
            && string.Equals(c.OldValue, current, StringComparison.Ordinal));

    /// <summary>
    /// A discrete credit without phantom delimiter segments, and the ids that travel with it — null
    /// unless the id list is positionally aligned with the stored list (a misaligned pair is worse
    /// than names alone).
    /// </summary>
    private static (List<string> Names, List<string>? Ids) DiscreteCredit(string? artists, string? artistMbids)
    {
        var raw = MultiValue.Split(artists);
        var rawIds = MultiValue.Split(artistMbids);
        var ids = rawIds.Length > 0 && rawIds.Length == raw.Length ? new List<string>(raw.Length) : null;
        var names = new List<string>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (ArtistCreditNormalizer.IsDelimiterToken(raw[i]))
                continue;
            names.Add(raw[i]);
            ids?.Add(rawIds[i]);
        }

        return (names, ids);
    }

    // The field's current value as the audit trail and any user revert last left it.
    private static ChangeRow? LastApplied(List<ChangeRow> history, string field) =>
        history.LastOrDefault(c => c.FieldName == field && c.Applied && !c.Reverted);

    // At most once per value: when a repair already moved this field away from exactly this value
    // and it came back — a user reverted the repair, or a writer this heal does not know about put it
    // back — leave it. That is what keeps this heal from ever joining an oscillation.
    private static bool AlreadyRepairedAway(List<ChangeRow> history, string field, string? current) =>
        history.Any(c => c.FieldName == field
            && RepairSources.Contains(c.Source)
            && string.Equals(c.OldValue, current, StringComparison.Ordinal));

    // A manual approval locks the match ("the pipeline won't touch it"). One given after the damage
    // vouched for the value as it then stood — the reviewer's edits leave no audit row to tell them
    // apart — so it stands; one given before did not stop the curation action either, and the
    // restore puts back what the approval had accepted.
    private static bool ApprovedSince(SongMetadata song, DateTime damagedAtUtc) =>
        song.IsManuallyApproved && !(song.ManuallyApprovedAtUtc < damagedAtUtc);

    private static void Apply(SongMetadata song, string field, string? value, string source, List<PlannedChange> changes)
    {
        song.CaptureOriginalMetadata();
        changes.Add(new PlannedChange(field, ValueOf(song, field), value, source));
        SongFieldReverter.Apply(song, field, value);
    }

    private static string? ValueOf(SongMetadata song, string field) => field switch
    {
        Artist => song.Artist,
        AlbumArtist => song.AlbumArtist,
        Artists => song.Artists,
        ArtistMusicBrainzIds => song.ArtistMusicBrainzIds,
        AlbumArtistMusicBrainzId => song.AlbumArtistMusicBrainzId,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    // Same exclusions as the other healers. Demo rows are seeded terminal with DestinationPath ==
    // SourcePath, and a re-queue would point the builder's delete at the read-only source mount.
    // Duplicates stay in: the merge and split that did the damage never excluded them, and their
    // names still feed the artist-dedup view.
    private IQueryable<SongMetadata> QueryEligible() => db.Songs
        .IgnoreQueryFilters()
        .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
        .ExcludingDemoTenant();

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

    private sealed record ChangeRow(
        int Id, int SongId, string FieldName, string Source, string? OldValue, string? NewValue,
        DateTime CreatedAtUtc, bool Applied, bool Reverted);

    private sealed record AliasRow(Guid OwnerUserId, string AliasKey, string CanonicalName, DateTime CreatedAtUtc);

    private sealed record PlannedChange(string Field, string? OldValue, string? NewValue, string Source);

    private sealed record SongPlan(SongMetadata Song, List<PlannedChange> Changes);

    private sealed record PlanContext(
        ILookup<Guid, AliasRow> AliasRows,
        ArtistAliasMap Aliases,
        IReadOnlyDictionary<(string ArtistKey, string AlbumKey), string> CanonicalArtists,
        ILookup<int, SongProviderAttempt> Attempts);
}
