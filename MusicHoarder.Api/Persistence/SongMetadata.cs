using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public record EnrichmentMatchData(
    string? Artist,
    string? AlbumArtist,
    string? Title,
    int? Year,
    int? TrackNumber,
    string? MusicBrainzId,
    string? MusicBrainzReleaseId,
    string? SpotifyId,
    string? AcoustIdTrackId,
    string? Isrc,
    string MatchedBy,
    double AdjustedScore,
    string? WarningsJson,
    EnrichmentStatus RecommendedStatus,
    string? Album = null,
    string? Artists = null,
    string? ArtistMusicBrainzIds = null,
    string? AlbumArtistMusicBrainzId = null,
    string? MusicBrainzReleaseGroupId = null,
    int? DiscNumber = null,
    int? TotalDiscs = null,
    int? TotalTracks = null,
    bool? IsCompilation = null,
    string? ReleaseTypePrimary = null,
    string? ReleaseTypes = null);

public class SongMetadata
{
    private const int MaxErrorLength = 1024;

    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Owner of this row. Scoped by the EF global query filter so users only ever see their own.
    /// Background services bypass the filter via <c>IgnoreQueryFilters()</c> and explicitly pass
    /// the owner id from <see cref="MusicHoarder.Api.Auth.IOwnerLookupService"/>.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// True for rows inserted by the demo seeder. Scanner reconciliation and LibraryBuilder skip
    /// these so we don't try to read a file off disk that doesn't exist.
    /// </summary>
    public bool IsSynthetic { get; set; }

    public required string SourcePath { get; set; }
    public required long FileSizeBytes { get; set; }
    public required string FileName { get; set; }
    public required string Extension { get; set; }
    public required DateTime LastModifiedUtc { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }

    /// <summary>Discrete track-artist names, ';'-joined (incl. featured), display order. See <see cref="Metadata.MultiValue"/>.</summary>
    public string? Artists { get; set; }

    /// <summary>Per-artist MusicBrainz IDs, ';'-joined, positionally aligned with <see cref="Artists"/>.</summary>
    public string? ArtistMusicBrainzIds { get; set; }

    public int? DiscNumber { get; set; }
    public int? TotalDiscs { get; set; }
    public int? TotalTracks { get; set; }

    /// <summary>
    /// Various-Artists / iTunes compilation flag. Drives the "Various Artists" album-artist
    /// substitution + COMPILATION tag at write time and the Various-Artists folder routing —
    /// the per-track <see cref="AlbumArtist"/> on the row stays the truthful primary.
    /// </summary>
    public bool IsCompilation { get; set; }

    /// <summary>MusicBrainz release-group primary type, lowercased (album|single|ep|broadcast|other).</summary>
    public string? ReleaseTypePrimary { get; set; }

    /// <summary>Full release type, ';'-joined lowercase primary + secondaries (e.g. "album; compilation").</summary>
    public string? ReleaseTypes { get; set; }

    public int? DurationSeconds { get; set; }
    public int? DurationMs { get; set; }
    public required DateTime IndexedAtUtc { get; set; }
    public string? Fingerprint { get; set; }
    public int? Bitrate { get; set; }

    /// <summary>
    /// True when the scanner found album artwork for this track — either embedded in the file
    /// or as a sibling <c>cover/folder/front.*</c> image in the source directory (Navidrome's
    /// resolution order). A fact about the file, refreshed on each re-scan; orthogonal to the
    /// enrichment/build lifecycle. The actual bytes are resolved on demand by the cover endpoint
    /// and the library builder, never persisted.
    /// </summary>
    public bool HasCoverArt { get; set; }

    // --- Duplicate detection ---

    public bool IsDuplicate { get; set; }
    public int? DuplicateOfId { get; set; }
    public SongMetadata? DuplicateOf { get; set; }

    public string? Isrc { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
    public string? MusicBrainzReleaseGroupId { get; set; }
    public string? AlbumArtistMusicBrainzId { get; set; }
    public string? SpotifyId { get; set; }
    public string? AcoustIdTrackId { get; set; }
    public string? LrclibId { get; set; }
    public EnrichmentStatus EnrichmentStatus { get; set; } = EnrichmentStatus.Pending;
    public string? MatchedBy { get; set; }
    public double? MatchConfidence { get; set; }
    public string? MatchWarnings { get; set; }
    public DateTime? EnrichedAtUtc { get; set; }
    public DateTime? EnrichmentLastAttemptedAtUtc { get; set; }
    public string? EnrichmentError { get; set; }

    /// <summary>
    /// When set, the user has explicitly approved/locked this song's match. The enrichment
    /// pipeline skips it and <see cref="ResetEnrichment"/> is a no-op unless forced, so a
    /// re-scan can never silently undo a curated decision.
    /// </summary>
    public bool IsManuallyApproved { get; set; }
    public DateTime? ManuallyApprovedAtUtc { get; set; }

    public bool OriginalMetadataCaptured { get; set; }
    public string? OriginalArtist { get; set; }
    public string? OriginalAlbumArtist { get; set; }
    public string? OriginalAlbum { get; set; }
    public string? OriginalTitle { get; set; }
    public int? OriginalYear { get; set; }
    public int? OriginalTrackNumber { get; set; }
    public string? OriginalIsrc { get; set; }
    public string? OriginalMusicBrainzId { get; set; }
    public string? OriginalSpotifyId { get; set; }
    public string? OriginalArtists { get; set; }
    public int? OriginalDiscNumber { get; set; }
    public int? OriginalTotalDiscs { get; set; }
    public int? OriginalTotalTracks { get; set; }
    public bool OriginalIsCompilation { get; set; }
    public string? OriginalReleaseTypePrimary { get; set; }
    public string? OriginalReleaseTypes { get; set; }
    public DateTime? OriginalMetadataCapturedAtUtc { get; set; }
    public bool IsUnreleased { get; set; }
    public LibraryBuildStatus LibraryBuildStatus { get; set; } = LibraryBuildStatus.Pending;
    public DateTime? LibraryBuiltAtUtc { get; set; }
    public DateTime? LibraryBuildLastAttemptedAtUtc { get; set; }
    public string? LibraryBuildError { get; set; }
    public string? DestinationPath { get; set; }
    public string? PreviousDestinationPath { get; set; }

    /// <summary>
    /// JSON snapshot of the tag set last physically written to the destination file (a serialized
    /// <see cref="Library.WrittenTagSet"/>). Each successful build diffs the about-to-be-written tags
    /// against this to emit <see cref="LibraryWriteEvent"/>s with accurate "since last time" old values,
    /// then overwrites it. Null until the first write; a re-fingerprint clears it.
    /// </summary>
    public string? LastWrittenTagsJson { get; set; }
    public DateTime? LastWrittenAtUtc { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    // --- Provider attempts ---

    public ICollection<SongProviderAttempt> ProviderAttempts { get; set; } = new List<SongProviderAttempt>();

    // --- Lyrics ---

    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }
    public bool? IsInstrumental { get; set; }
    public LyricsStatus LyricsStatus { get; set; } = LyricsStatus.NotFetched;

    // --- Guard properties ---

    public bool IsDeleted => DeletedAtUtc.HasValue;

    public bool IsReadyForEnrichment =>
        !IsDeleted
        && EnrichmentStatus == EnrichmentStatus.Pending
        && (
            (!string.IsNullOrWhiteSpace(Fingerprint) && DurationSeconds is not null)
            || (!string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Title))
            || !string.IsNullOrWhiteSpace(Isrc));

    public bool IsReadyForBuild =>
        !IsDeleted
        && !IsDuplicate
        && EnrichmentStatus == EnrichmentStatus.Matched
        && LibraryBuildStatus != LibraryBuildStatus.Done;

    public string TrackLabel
    {
        get
        {
            var artist = string.IsNullOrWhiteSpace(Artist) ? "<unknown-artist>" : Artist;
            var title = string.IsNullOrWhiteSpace(Title) ? "<unknown-title>" : Title;
            return $"{artist} - {title} [{FileName}]";
        }
    }

    // --- Enrichment lifecycle ---

    public void RecordEnrichmentAttempt()
    {
        EnrichmentLastAttemptedAtUtc = DateTime.UtcNow;
    }

    public void CaptureOriginalMetadata()
    {
        if (OriginalMetadataCaptured) return;

        OriginalMetadataCaptured = true;
        OriginalArtist = Artist;
        OriginalAlbumArtist = AlbumArtist;
        OriginalAlbum = Album;
        OriginalTitle = Title;
        OriginalYear = Year;
        OriginalTrackNumber = TrackNumber;
        OriginalIsrc = Isrc;
        OriginalMusicBrainzId = MusicBrainzId;
        OriginalSpotifyId = SpotifyId;
        OriginalArtists = Artists;
        OriginalDiscNumber = DiscNumber;
        OriginalTotalDiscs = TotalDiscs;
        OriginalTotalTracks = TotalTracks;
        OriginalIsCompilation = IsCompilation;
        OriginalReleaseTypePrimary = ReleaseTypePrimary;
        OriginalReleaseTypes = ReleaseTypes;
        OriginalMetadataCapturedAtUtc = DateTime.UtcNow;
    }

    public void RestoreOriginalMetadata()
    {
        if (!OriginalMetadataCaptured) return;

        Artist = OriginalArtist;
        AlbumArtist = OriginalAlbumArtist;
        Album = OriginalAlbum;
        Title = OriginalTitle;
        Year = OriginalYear;
        TrackNumber = OriginalTrackNumber;
        Isrc = OriginalIsrc;
        MusicBrainzId = OriginalMusicBrainzId;
        SpotifyId = OriginalSpotifyId;
        Artists = OriginalArtists;
        DiscNumber = OriginalDiscNumber;
        TotalDiscs = OriginalTotalDiscs;
        TotalTracks = OriginalTotalTracks;
        IsCompilation = OriginalIsCompilation;
        ReleaseTypePrimary = OriginalReleaseTypePrimary;
        ReleaseTypes = OriginalReleaseTypes;
    }

    /// <summary>
    /// Applies build-time canonical-album corrections to this row so the app view and the on-disk tags
    /// agree on one album: the unified album title/year and the canonical track/disc number. Used when
    /// an album's tracks were each enriched against a different release and so carry inconsistent
    /// years / track numbers; the canonical (multi-provider) tracklist is the source of truth.
    /// Reversible — captures originals first, so <see cref="ResetEnrichment"/> with
    /// <c>restoreOriginal</c> restores them. Deliberately does NOT touch <see cref="EnrichmentStatus"/>,
    /// <see cref="EnrichedAtUtc"/>, <see cref="MatchConfidence"/> or any grade, so it never triggers
    /// re-enrichment or an auto-regrade (grade staleness stays opt-in). Returns the field-level changes
    /// it made (empty when nothing changed) so the caller can record them in the change log.
    /// </summary>
    public IReadOnlyList<(string Field, string? OldValue, string? NewValue)> ApplyCanonicalCorrection(
        string? album, int? year, int? trackNumber, int? discNumber)
    {
        var changes = new List<(string, string?, string?)>();
        CaptureOriginalMetadata();

        if (!string.IsNullOrWhiteSpace(album) && !string.Equals(album, Album, StringComparison.Ordinal))
        {
            changes.Add((nameof(Album), Album, album));
            Album = album;
        }

        if (year is > 0 && year != Year)
        {
            changes.Add((nameof(Year), Year?.ToString(), year.Value.ToString()));
            Year = year;
        }

        if (trackNumber is > 0 && trackNumber != TrackNumber)
        {
            changes.Add((nameof(TrackNumber), TrackNumber?.ToString(), trackNumber.Value.ToString()));
            TrackNumber = trackNumber;
        }

        if (discNumber is > 0 && discNumber != DiscNumber)
        {
            changes.Add((nameof(DiscNumber), DiscNumber?.ToString(), discNumber.Value.ToString()));
            DiscNumber = discNumber;
        }

        return changes;
    }

    /// <summary>
    /// Persists a reconciler-elected <see cref="Library.AlbumIdentity"/> to this row so all tracks
    /// of one logical album carry the same album-level fields — and therefore resolve to the same
    /// destination folder and the same release id on disk (what keeps Navidrome from splitting the
    /// album). Album-level fields only: track-level fields (title, track/disc number, recording id,
    /// ISRC, artists) are never touched — the same guarantee <see cref="Library.AlbumIdentity"/>
    /// encodes at compile time. Only sets a field when the elected value is present and differs, so
    /// it never clears a member's value and repeated application converges to zero changes.
    /// Reversible — captures originals first. Deliberately does NOT touch
    /// <see cref="EnrichmentStatus"/>, <see cref="EnrichedAtUtc"/>, <see cref="MatchConfidence"/>
    /// or any grade, so it never triggers re-enrichment or an auto-regrade (grade staleness stays
    /// opt-in). Returns the field-level changes (empty when nothing changed) for the change log.
    /// </summary>
    public IReadOnlyList<(string Field, string? OldValue, string? NewValue)> ApplyIdentityCorrection(
        Library.AlbumIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var changes = new List<(string, string?, string?)>();
        CaptureOriginalMetadata();

        if (!string.IsNullOrWhiteSpace(identity.Album) && !string.Equals(identity.Album, Album, StringComparison.Ordinal))
        {
            changes.Add((nameof(Album), Album, identity.Album));
            Album = identity.Album;
        }

        if (!string.IsNullOrWhiteSpace(identity.AlbumArtist) && !string.Equals(identity.AlbumArtist, AlbumArtist, StringComparison.Ordinal))
        {
            changes.Add((nameof(AlbumArtist), AlbumArtist, identity.AlbumArtist));
            AlbumArtist = identity.AlbumArtist;
        }

        if (identity.Year is > 0 && identity.Year != Year)
        {
            changes.Add((nameof(Year), Year?.ToString(), identity.Year.Value.ToString()));
            Year = identity.Year;
        }

        // Compilation is additive in the election (any member true wins), so only ever flip false→true.
        if (identity.IsCompilation && !IsCompilation)
        {
            changes.Add((nameof(IsCompilation), IsCompilation.ToString(), identity.IsCompilation.ToString()));
            IsCompilation = true;
        }

        if (identity.TotalDiscs is > 0 && identity.TotalDiscs != TotalDiscs)
        {
            changes.Add((nameof(TotalDiscs), TotalDiscs?.ToString(), identity.TotalDiscs.Value.ToString()));
            TotalDiscs = identity.TotalDiscs;
        }

        if (!string.IsNullOrWhiteSpace(identity.ReleaseTypePrimary) && !string.Equals(identity.ReleaseTypePrimary, ReleaseTypePrimary, StringComparison.Ordinal))
        {
            changes.Add((nameof(ReleaseTypePrimary), ReleaseTypePrimary, identity.ReleaseTypePrimary));
            ReleaseTypePrimary = identity.ReleaseTypePrimary;
        }

        if (!string.IsNullOrWhiteSpace(identity.ReleaseTypes) && !string.Equals(identity.ReleaseTypes, ReleaseTypes, StringComparison.Ordinal))
        {
            changes.Add((nameof(ReleaseTypes), ReleaseTypes, identity.ReleaseTypes));
            ReleaseTypes = identity.ReleaseTypes;
        }

        if (!string.IsNullOrWhiteSpace(identity.MusicBrainzReleaseId) && !string.Equals(identity.MusicBrainzReleaseId, MusicBrainzReleaseId, StringComparison.Ordinal))
        {
            changes.Add((nameof(MusicBrainzReleaseId), MusicBrainzReleaseId, identity.MusicBrainzReleaseId));
            MusicBrainzReleaseId = identity.MusicBrainzReleaseId;
        }

        if (!string.IsNullOrWhiteSpace(identity.MusicBrainzReleaseGroupId) && !string.Equals(identity.MusicBrainzReleaseGroupId, MusicBrainzReleaseGroupId, StringComparison.Ordinal))
        {
            changes.Add((nameof(MusicBrainzReleaseGroupId), MusicBrainzReleaseGroupId, identity.MusicBrainzReleaseGroupId));
            MusicBrainzReleaseGroupId = identity.MusicBrainzReleaseGroupId;
        }

        if (!string.IsNullOrWhiteSpace(identity.AlbumArtistMusicBrainzId) && !string.Equals(identity.AlbumArtistMusicBrainzId, AlbumArtistMusicBrainzId, StringComparison.Ordinal))
        {
            changes.Add((nameof(AlbumArtistMusicBrainzId), AlbumArtistMusicBrainzId, identity.AlbumArtistMusicBrainzId));
            AlbumArtistMusicBrainzId = identity.AlbumArtistMusicBrainzId;
        }

        return changes;
    }

    public void ApplyEnrichmentMatch(EnrichmentMatchData match)
    {
        CaptureOriginalMetadata();

        Artist = string.IsNullOrWhiteSpace(match.Artist) ? Artist : match.Artist;
        AlbumArtist = string.IsNullOrWhiteSpace(match.AlbumArtist) ? AlbumArtist : match.AlbumArtist;
        Title = string.IsNullOrWhiteSpace(match.Title) ? Title : match.Title;
        Album = string.IsNullOrWhiteSpace(match.Album) ? Album : match.Album;
        if (match.Year is not null) Year = match.Year;
        if (match.TrackNumber is not null) TrackNumber = match.TrackNumber;
        Artists = string.IsNullOrWhiteSpace(match.Artists) ? Artists : match.Artists;
        if (match.DiscNumber is not null) DiscNumber = match.DiscNumber;
        if (match.TotalDiscs is not null) TotalDiscs = match.TotalDiscs;
        if (match.TotalTracks is not null) TotalTracks = match.TotalTracks;
        if (match.IsCompilation is not null) IsCompilation = match.IsCompilation.Value;
        if (!string.IsNullOrWhiteSpace(match.ReleaseTypePrimary)) ReleaseTypePrimary = match.ReleaseTypePrimary;
        if (!string.IsNullOrWhiteSpace(match.ReleaseTypes)) ReleaseTypes = match.ReleaseTypes;
        MusicBrainzId = match.MusicBrainzId ?? MusicBrainzId;
        MusicBrainzReleaseId = match.MusicBrainzReleaseId ?? MusicBrainzReleaseId;
        MusicBrainzReleaseGroupId = match.MusicBrainzReleaseGroupId ?? MusicBrainzReleaseGroupId;
        AlbumArtistMusicBrainzId = match.AlbumArtistMusicBrainzId ?? AlbumArtistMusicBrainzId;
        ArtistMusicBrainzIds = string.IsNullOrWhiteSpace(match.ArtistMusicBrainzIds) ? ArtistMusicBrainzIds : match.ArtistMusicBrainzIds;
        SpotifyId = match.SpotifyId ?? SpotifyId;
        AcoustIdTrackId = match.AcoustIdTrackId ?? AcoustIdTrackId;
        if (!string.IsNullOrWhiteSpace(match.Isrc)) Isrc = match.Isrc;
        MatchedBy = match.MatchedBy;
        MatchConfidence = match.AdjustedScore;
        MatchWarnings = match.WarningsJson;
        EnrichmentStatus = match.RecommendedStatus;
        EnrichedAtUtc = DateTime.UtcNow;
        EnrichmentError = null;
    }

    public void MarkEnrichmentNeedsReview(string reason)
    {
        var now = DateTime.UtcNow;
        EnrichmentStatus = EnrichmentStatus.NeedsReview;
        EnrichmentLastAttemptedAtUtc = now;
        EnrichedAtUtc = now;
        EnrichmentError = reason;
        MatchedBy = null;
        MatchConfidence = null;
        MatchWarnings = null;
    }

    // Records a provider's sub-threshold/needs-review hit on the row's review-bookkeeping
    // fields without overwriting Artist/Title/Album/IDs. Only "promotes" the row's
    // MatchedBy/MatchConfidence when the new confidence beats the previously-recorded one,
    // so the row tracks the best available candidate for review and bulk-approve.
    public void MarkProviderNeedsReview(string matchedBy, double confidence, string? warningsJson)
    {
        EnrichmentStatus = EnrichmentStatus.NeedsReview;
        EnrichedAtUtc = DateTime.UtcNow;
        EnrichmentError = null;

        if (MatchConfidence is null || confidence > MatchConfidence.Value)
        {
            MatchedBy = matchedBy;
            MatchConfidence = confidence;
            MatchWarnings = warningsJson;
        }
    }

    public void MarkEnrichmentFailed(string error)
    {
        var now = DateTime.UtcNow;
        EnrichmentStatus = EnrichmentStatus.Failed;
        EnrichmentError = Truncate(error, MaxErrorLength);
        EnrichmentLastAttemptedAtUtc = now;
        EnrichedAtUtc = now;
    }

    /// <summary>Locks the song's match so the pipeline won't touch it and resets can't undo it.</summary>
    public void LockManualApproval()
    {
        IsManuallyApproved = true;
        ManuallyApprovedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Clears the manual-approval lock, allowing the pipeline to re-enrich it.</summary>
    public void UnlockManualApproval()
    {
        IsManuallyApproved = false;
        ManuallyApprovedAtUtc = null;
    }

    public void ResetEnrichment(bool restoreOriginal = true, bool force = false)
    {
        // Honor a manual-approval lock unless explicitly forced (e.g. an "unlock & reset" action).
        if (IsManuallyApproved && !force)
            return;

        if (force)
            UnlockManualApproval();

        if (restoreOriginal)
            RestoreOriginalMetadata();

        EnrichmentStatus = EnrichmentStatus.Pending;
        MatchedBy = null;
        MatchConfidence = null;
        MatchWarnings = null;
        EnrichedAtUtc = null;
        EnrichmentLastAttemptedAtUtc = null;
        EnrichmentError = null;
        AcoustIdTrackId = null;
        MusicBrainzReleaseId = null;
        MusicBrainzReleaseGroupId = null;
        AlbumArtistMusicBrainzId = null;
        ArtistMusicBrainzIds = null;

        ProviderAttempts.Clear();

        ResetLyrics();
    }

    /// <summary>
    /// Derives the summary <see cref="EnrichmentStatus"/> from the set of
    /// <see cref="ProviderAttempts"/> for this song and the list of enabled providers.
    /// Delegates to <see cref="Enrichment.ConsensusEvaluator"/> so a single (unreliable)
    /// AcoustID hit can no longer mark a song Matched on its own — corroboration is required.
    /// </summary>
    public EnrichmentStatus ComputeSummaryStatus(IReadOnlySet<EnrichmentProvider> enabledProviders)
        => Enrichment.ConsensusEvaluator
            .Evaluate(this, enabledProviders, Enrichment.ConsensusEvaluator.DefaultIdentityOptions)
            .Status;

    // --- Duplicate detection lifecycle ---

    public void MarkAsDuplicate(int duplicateOfId)
    {
        IsDuplicate = true;
        DuplicateOfId = duplicateOfId;
    }

    public void ClearDuplicate()
    {
        IsDuplicate = false;
        DuplicateOfId = null;
    }

    // --- Library build lifecycle ---

    public void MarkCopied()
    {
        LibraryBuildStatus = LibraryBuildStatus.Copied;
        LibraryBuildError = null;
    }

    public void MarkTagged()
    {
        LibraryBuildStatus = LibraryBuildStatus.Tagged;
    }

    public void MarkBuildDone(string destinationPath)
    {
        LibraryBuildStatus = LibraryBuildStatus.Done;
        LibraryBuiltAtUtc = DateTime.UtcNow;
        LibraryBuildError = null;
        DestinationPath = destinationPath;
        PreviousDestinationPath = null;
    }

    public void MarkBuildFailed(string error)
    {
        LibraryBuildStatus = LibraryBuildStatus.Failed;
        LibraryBuildError = Truncate(error, MaxErrorLength);
        LibraryBuiltAtUtc = null;
        LibraryBuildLastAttemptedAtUtc = DateTime.UtcNow;
    }

    public void ResetLibraryBuild()
    {
        LibraryBuildStatus = LibraryBuildStatus.Pending;
        LibraryBuiltAtUtc = null;
        LibraryBuildLastAttemptedAtUtc = null;
        LibraryBuildError = null;
        PreviousDestinationPath = DestinationPath;
        DestinationPath = null;
    }

    /// <summary>
    /// Re-queues an already-built track so the next build re-copies and re-tags its destination file
    /// in place — WITHOUT touching enrichment. Keeps <see cref="DestinationPath"/> and points
    /// <see cref="PreviousDestinationPath"/> at it: that's the signal the builder's skip-copy fast path
    /// uses to force a real re-copy + re-tag instead of treating a same-size destination as "already
    /// built". Crucial because re-tagging a FLAC leaves its size identical (padding block), so without
    /// this the rewrite would be silently skipped. The previous == current path means no folder
    /// move/prune is triggered. Used to apply new tag-writing logic to files that already built.
    /// </summary>
    public void RequeueForRetag()
    {
        LibraryBuildStatus = LibraryBuildStatus.Pending;
        LibraryBuiltAtUtc = null;
        LibraryBuildLastAttemptedAtUtc = null;
        LibraryBuildError = null;
        PreviousDestinationPath = DestinationPath;
    }

    public void ResetPostFingerprint()
    {
        ResetEnrichment(restoreOriginal: true);
        ResetLibraryBuild();
        PreviousDestinationPath = null;
        IsDuplicate = false;
        DuplicateOfId = null;
        IsUnreleased = false;
        // A re-fingerprint invalidates everything we knew about prior writes; drop the snapshot so the
        // next build diffs from the source-original baseline rather than a stale written state.
        LastWrittenTagsJson = null;
        LastWrittenAtUtc = null;
    }

    // --- Lyrics lifecycle ---

    public bool IsReadyForLyricsFetch =>
        !IsDeleted
        && (EnrichmentStatus == EnrichmentStatus.Matched || EnrichmentStatus == EnrichmentStatus.NeedsReview)
        && LyricsStatus == LyricsStatus.NotFetched
        && !string.IsNullOrWhiteSpace(Title)
        && !string.IsNullOrWhiteSpace(Artist);

    public void ApplyLyricsResult(string? syncedLyrics, string? plainLyrics, bool instrumental, int? lrclibId = null)
    {
        IsInstrumental = instrumental;
        if (lrclibId is not null) LrclibId = lrclibId.Value.ToString();
        if (instrumental)
        {
            LyricsStatus = LyricsStatus.Instrumental;
            SyncedLyrics = null;
            PlainLyrics = null;
            return;
        }

        SyncedLyrics = string.IsNullOrWhiteSpace(syncedLyrics) ? null : syncedLyrics;
        PlainLyrics = string.IsNullOrWhiteSpace(plainLyrics) ? null : plainLyrics;

        if (SyncedLyrics is null && PlainLyrics is null)
        {
            LyricsStatus = LyricsStatus.NotFound;
        }
        else
        {
            LyricsStatus = LyricsStatus.Fetched;
        }
    }

    public void MarkLyricsNotFound()
    {
        LyricsStatus = LyricsStatus.NotFound;
        SyncedLyrics = null;
        PlainLyrics = null;
    }

    public void MarkLyricsFailed()
    {
        LyricsStatus = LyricsStatus.Failed;
    }

    public void ResetLyrics()
    {
        LyricsStatus = LyricsStatus.NotFetched;
        SyncedLyrics = null;
        PlainLyrics = null;
        IsInstrumental = null;
        LrclibId = null;
    }

    // --- Soft delete ---

    public void SoftDelete()
    {
        DeletedAtUtc = DateTime.UtcNow;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

public enum EnrichmentStatus
{
    Pending = 0,
    Matched = 1,
    NeedsReview = 2,
    Failed = 3,
}

public enum LibraryBuildStatus
{
    Pending = 0,
    Copied = 1,
    Tagged = 2,
    Done = 3,
    Failed = 4,
}

public enum LyricsStatus
{
    NotFetched = 0,
    Fetched = 1,
    Instrumental = 2,
    NotFound = 3,
    Failed = 4,
}
