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
    string? ReleaseTypes = null,
    string? Genre = null,
    string? ReleaseDate = null,
    string? OriginalReleaseDate = null,
    string? Label = null,
    string? CatalogNumber = null,
    string? Upc = null,
    string? Composer = null,
    string? Copyright = null,
    string? ArtistSort = null,
    string? AlbumArtistSort = null);

/// <summary>
/// Why this track is in the collection — did the owner ask for it, or did the app add it on their
/// behalf? <see cref="Explicit"/> is deliberately <c>0</c>: it is the column default, so every row
/// that predates album completion (and every scanned, synced or user-requested row after it) is
/// "mine" with no backfill. Absence of information means the owner wanted it.
/// </summary>
public enum SongAcquisitionIntent
{
    /// <summary>The owner asked for this track — a scanned source file, a Spotify like, a playlist, a URL import.</summary>
    Explicit = 0,

    /// <summary>
    /// Added only because the owner already had another track from the same album (see
    /// <c>AlbumCompletionSweep</c>). Shown in All tracks and the album view, excluded from "My music"
    /// until the owner likes it.
    /// </summary>
    AlbumFill = 1,
}

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

    // --- Descriptive metadata (enrichment-sourced; SpotiFLAC-inspired) ---
    //
    // Populated by the enrichment providers (chiefly MusicBrainz; Copyright from Spotify), never by the
    // scanner, so there is no captured-original to restore — ResetEnrichment nulls them like the other
    // enrichment-derived fields. All are ';'-joined multi-value where the tag is multi-valued (Genre),
    // otherwise a single value. Written to the destination file by TagLibLibraryTagWriter.

    /// <summary>Genres, ';'-joined multi-value (e.g. "Hip Hop; Rap"). See <see cref="Metadata.MultiValue"/>.</summary>
    public string? Genre { get; set; }

    /// <summary>Full release date as an ISO string (YYYY-MM-DD, or a partial "YYYY"/"YYYY-MM"); <see cref="Year"/> stays the coarse int form.</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>Original (first) release date of the release-group, ISO string — the ORIGINALDATE tag; distinguishes a reissue from the first pressing.</summary>
    public string? OriginalReleaseDate { get; set; }

    /// <summary>Record label / publisher (album-level).</summary>
    public string? Label { get; set; }

    /// <summary>Label catalog number (album-level).</summary>
    public string? CatalogNumber { get; set; }

    /// <summary>Album barcode / UPC (album-level).</summary>
    public string? Upc { get; set; }

    /// <summary>Composer / songwriter credit (track-level).</summary>
    public string? Composer { get; set; }

    /// <summary>Copyright line — the © line, not the ℗ phonogram line (album-level).</summary>
    public string? Copyright { get; set; }

    /// <summary>Sort name for the display artist (ARTISTSORT), e.g. "Beatles, The".</summary>
    public string? ArtistSort { get; set; }

    /// <summary>Sort name for the album artist (ALBUMARTISTSORT).</summary>
    public string? AlbumArtistSort { get; set; }

    public int? DurationSeconds { get; set; }
    public int? DurationMs { get; set; }
    public required DateTime IndexedAtUtc { get; set; }

    /// <summary>
    /// When this track entered the collection — stamped ONCE, when the row is first created, and never
    /// rewritten afterwards. This is the field "recently added" views must sort on.
    ///
    /// The two timestamps that look like they'd do the job don't: <see cref="IndexedAtUtc"/> is bumped
    /// whenever the file changes on disk and gets re-indexed (an external tag edit is enough), and
    /// <see cref="LibraryBuiltAtUtc"/> is cleared by <see cref="ResetLibraryBuild"/> — which a re-index,
    /// a re-enrichment or a source upgrade all trigger. Either one makes an album the user has owned for
    /// years jump to the top of "recently added" for no reason the user can see.
    ///
    /// Seeded from <c>min(file mtime, index time)</c>: a first bulk import would otherwise collapse the
    /// whole back catalogue into one instant, whereas the filesystem usually still remembers roughly when
    /// each file arrived. The <c>min</c> keeps a bogus future mtime from winning.
    /// </summary>
    public DateTime? AcquiredAtUtc { get; set; }

    /// <summary>
    /// The acquisition stamp to seed a freshly created row with — see <see cref="AcquiredAtUtc"/>.
    /// </summary>
    public static DateTime SeedAcquiredAt(DateTime lastModifiedUtc, DateTime indexedAtUtc) =>
        lastModifiedUtc < indexedAtUtc ? lastModifiedUtc : indexedAtUtc;

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

    /// <summary>
    /// When set, the user explicitly chose this song as the keeper of its duplicate group.
    /// Detection re-runs rank a pinned song above any automatic quality election so a manual
    /// choice is never overturned. Cleared on re-fingerprint (the audio identity changed).
    /// </summary>
    public DateTime? DuplicateKeeperPinnedAtUtc { get; set; }

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
    /// The <see cref="Enrichment.EnrichmentAlgorithm.CurrentVersion"/> this row was last processed under.
    /// Stamped by the orchestrator on every terminal verdict; rows in NeedsReview/Failed whose value is
    /// behind the current version are auto-re-enriched by the startup sweep when the algorithm changes.
    /// Defaults to 0 so pre-versioning rows are picked up on the first bump.
    /// </summary>
    public int LastEnrichmentAlgorithmVersion { get; set; }

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
    /// <summary>
    /// Consecutive failed library-build attempts. Incremented by <see cref="MarkBuildFailed"/> and
    /// reset to zero on a successful build or any reset/requeue. Once it reaches
    /// <c>MaxLibraryBuildAttempts</c> the build query quarantines the row so a persistently
    /// un-writable file can't loop the builder forever (issue #239).
    /// </summary>
    public int LibraryBuildAttempts { get; set; }
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

    // --- Listening signals (user data, not pipeline state) ---
    //
    // Deliberately absent from RebuildOnMetadataChangeInterceptor.TagRelevantProperties and never
    // touched by ResetEnrichment/RequeueForRetag: a like or play must survive re-enrichment and
    // re-builds, and must never re-tag the destination file.

    /// <summary>
    /// Whether the owner asked for this track or the app added it to complete an album. Written once,
    /// by <c>WishlistDownloadProcessor.LinkDownloadedItemsAsync</c>, when a wishlist item is linked to
    /// its ingested song — never by enrichment. This is the authoritative fact the "My music" view
    /// filters on; <see cref="Library.SongOriginResolver"/>'s derived origin is display-only and can
    /// lose the link (soft-delete, upgrade merge, wishlist deletion), which is exactly why this is a
    /// column and not another derivation.
    /// </summary>
    public SongAcquisitionIntent AcquisitionIntent { get; set; } = SongAcquisitionIntent.Explicit;

    /// <summary>When the user liked this song; null = not liked. Doubles as the "recently liked" sort key.</summary>
    public DateTime? LikedAtUtc { get; set; }

    public int PlayCount { get; set; }
    public DateTime? LastPlayedAtUtc { get; set; }

    // --- Navidrome like sync (two-way with Subsonic "starred") ---
    //
    // Sync bookkeeping for keeping LikedAtUtc in sync with a Navidrome server. Like the like itself,
    // these survive re-enrichment/rebuilds and never re-tag the destination file.

    /// <summary>
    /// The liked state agreed with Navidrome at the last successful reconcile — the base of the
    /// three-way merge that tells a local like/unlike apart from a remote one. Null until the song has
    /// ever participated in a like sync.
    /// </summary>
    public bool? LikeLastSyncedValue { get; set; }

    /// <summary>
    /// Cached Navidrome (Subsonic) <c>media_file</c> id, resolved during sync by path/MBID/fuzzy
    /// match. An optimization for star/unstar; the reconciler re-resolves it when null.
    /// </summary>
    public string? NavidromeSongId { get; set; }

    /// <summary>Whether this song is liked — the boolean view of <see cref="LikedAtUtc"/> the like sync uses.</summary>
    public bool IsLiked => LikedAtUtc is not null;

    /// <summary>
    /// Sets or clears the like, stamping <see cref="LikedAtUtc"/> when it flips to liked and clearing it
    /// when it flips to unliked. Idempotent (keeps the existing timestamp). Returns true when the liked
    /// state actually changed. Used by the like sync's Navidrome → MH pull direction.
    /// </summary>
    public bool SetLiked(bool liked)
    {
        if (liked == IsLiked) return false;
        LikedAtUtc = liked ? DateTime.UtcNow : null;
        return true;
    }

    /// <summary>Records the like value the sync has reconciled with Navidrome (the merge base).</summary>
    public void MarkLikeSynced(bool liked) => LikeLastSyncedValue = liked;

    // --- Provider attempts ---

    public ICollection<SongProviderAttempt> ProviderAttempts { get; set; } = new List<SongProviderAttempt>();

    /// <summary>The optional YouTube music video attached to this song (one per song).</summary>
    public SongMusicVideo? MusicVideo { get; set; }

    // --- Lyrics ---

    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }
    public bool? IsInstrumental { get; set; }
    public LyricsStatus LyricsStatus { get; set; } = LyricsStatus.NotFetched;

    /// <summary>When LRCLIB was last queried for this song (any outcome). Diagnostics only.</summary>
    public DateTime? LyricsLastAttemptedAtUtc { get; set; }

    /// <summary>
    /// How many times LRCLIB has been queried for this song. Drives the exponential backoff between
    /// re-checks so a track LRCLIB genuinely doesn't carry is asked about ever more rarely.
    /// </summary>
    public int LyricsFetchAttempts { get; set; }

    /// <summary>
    /// When this song may be re-queried against LRCLIB, or null when it never should be. LRCLIB is a
    /// community database that grows over time: a track that 404'd (or that only had unsynced lyrics)
    /// when we first asked may have gained an LRC since, and nothing else in the pipeline would ever
    /// ask again — the backfill sweep only covers <see cref="LyricsStatus.NotFetched"/>. Set by
    /// <see cref="ScheduleLyricsRecheck"/> on every attempt; the SQL mirror of the eligibility rule
    /// lives in <c>EnrichmentQueries.WhereReadyForLyricsRecheck</c>.
    /// </summary>
    public DateTime? LyricsNextRecheckAfterUtc { get; set; }

    // --- AI lyrics transcription (experimental; stored SEPARATELY from the LRCLIB lyrics above) ---
    //
    // An AI transcription of the song's audio (OpenAI whisper-1) kept apart from SyncedLyrics/PlainLyrics
    // so it never clobbers the curated LRCLIB lyrics and the UI can show the two side-by-side. These
    // fields are deliberately NOT in RebuildOnMetadataChangeInterceptor.TagRelevantProperties — storing a
    // transcription must never re-tag the destination file (the transcript is for comparison, not the file).

    public string? TranscribedSyncedLyrics { get; set; }
    public string? TranscribedPlainLyrics { get; set; }
    public TranscriptionStatus TranscriptionStatus { get; set; } = TranscriptionStatus.NotRequested;
    public DateTime? TranscribedAtUtc { get; set; }
    public string? TranscriptionModel { get; set; }
    public string? TranscriptionError { get; set; }

    /// <summary>
    /// Which lyrics the app's synced viewer shows when both an LRCLIB version and an AI transcription
    /// exist — a display preference the user sets from the side-by-side comparison. Defaults to the
    /// curated LRCLIB version. Display-only: it never changes what is embedded into the destination file
    /// (so it is intentionally absent from <c>RebuildOnMetadataChangeInterceptor.TagRelevantProperties</c>).
    /// </summary>
    public PreferredLyricsSource PreferredLyricsSource { get; set; } = PreferredLyricsSource.Lrclib;

    /// <summary>
    /// The lyrics that are canonical for display AND embedded into the destination file: the AI
    /// transcription when the user chose it via <see cref="PreferredLyricsSource"/> (and it exists),
    /// otherwise the LRCLIB version. Computed (not mapped) and non-destructive — both versions are kept,
    /// so the choice can be switched at any time and the file re-tagged accordingly.
    /// </summary>
    public string? EffectiveSyncedLyrics =>
        PreferredLyricsSource == PreferredLyricsSource.Transcribed && !string.IsNullOrWhiteSpace(TranscribedSyncedLyrics)
            ? TranscribedSyncedLyrics
            : SyncedLyrics;

    public string? EffectivePlainLyrics =>
        PreferredLyricsSource == PreferredLyricsSource.Transcribed && !string.IsNullOrWhiteSpace(TranscribedPlainLyrics)
            ? TranscribedPlainLyrics
            : PlainLyrics;

    /// <summary>
    /// The lyrics read-only display surfaces (the public share page) present — mirrors the in-app
    /// viewer: the AI transcription when the user chose it via <see cref="PreferredLyricsSource"/>
    /// OR when it is the only version that exists (the usual reason to transcribe is that LRCLIB
    /// had nothing), otherwise the LRCLIB version. The source is picked as a whole so one
    /// version's synced lines are never mixed with the other's plain text. Unlike
    /// <see cref="EffectiveSyncedLyrics"/> this never affects what is embedded into files.
    /// </summary>
    public string? DisplaySyncedLyrics => UseTranscribedForDisplay ? TranscribedSyncedLyrics : SyncedLyrics;

    public string? DisplayPlainLyrics => UseTranscribedForDisplay ? TranscribedPlainLyrics : PlainLyrics;

    private bool UseTranscribedForDisplay =>
        (!string.IsNullOrWhiteSpace(TranscribedSyncedLyrics) || !string.IsNullOrWhiteSpace(TranscribedPlainLyrics))
        && (PreferredLyricsSource == PreferredLyricsSource.Transcribed
            || (string.IsNullOrWhiteSpace(SyncedLyrics) && string.IsNullOrWhiteSpace(PlainLyrics)));

    /// <summary>
    /// True when the stored transcription carries the song's <b>official</b> lyric text re-timed against
    /// the audio (a successful forced/LLM alignment to the LRCLIB words) rather than the AI's own guess at
    /// the words. This is the single bit that separates an <i>AI enhanced</i> lyric from an <i>AI generated</i>
    /// one, so it is persisted rather than recomputed — the alignment inputs (Whisper's word clock) are gone
    /// once the request ends. See <see cref="LyricsProvenance"/>.
    /// </summary>
    public bool TranscriptionAlignedToReference { get; set; }

    // --- Lyrics timing validation ---
    //
    // LRCLIB is a community database keyed on track name; its /api/search fallback can return an LRC that
    // belongs to a DIFFERENT recording of the same song (a live cut, a sped-up edit, an extended mix), whose
    // timestamps are wildly wrong for our audio. These columns record whether a stored LRC's timing has been
    // checked against the track, and what the check concluded. Display-only — a Suspect verdict never hides
    // the lyrics, it only labels them and queues the AI probe.

    /// <summary>The track length LRCLIB reported for the matched entry, in seconds. Null before the fix landed.</summary>
    public double? LrclibDurationSeconds { get; set; }

    public LyricsSyncStatus LyricsSyncStatus { get; set; } = LyricsSyncStatus.NotChecked;

    /// <summary>Short human-readable reason for a non-Ok verdict, e.g. "lyrics run 48s past the end of the track".</summary>
    public string? LyricsSyncIssue { get; set; }

    public DateTime? LyricsSyncCheckedAtUtc { get; set; }

    /// <summary>
    /// The constant shift the AI probe measured and applied to <see cref="SyncedLyrics"/>, in milliseconds
    /// (positive = the stored LRC was running early and every line was pushed later). Non-null means the
    /// displayed LRCLIB lyrics carry AI-derived timing, which is what makes them <i>AI enhanced</i>.
    /// </summary>
    public int? LyricsSyncOffsetMs { get; set; }

    /// <summary>Fraction of probed reference words the transcript agreed with, 0-1. Diagnostics + label confidence.</summary>
    public double? LyricsSyncConfidence { get; set; }

    /// <summary>How many times the paid AI timing probe has run for this song. Bounds the retry on a Suspect row.</summary>
    public int LyricsSyncProbeAttempts { get; set; }

    /// <summary>
    /// How much AI is in the lyrics the app is showing right now — the value the badge in the web player,
    /// the share page and the Android viewer renders. Derived, never stored, so a preferred-source flip or a
    /// re-fetch can never leave a stale label behind.
    /// </summary>
    public LyricsProvenance LyricsProvenance => ComputeLyricsProvenance(
        UseTranscribedForDisplay,
        TranscriptionAlignedToReference,
        !string.IsNullOrWhiteSpace(SyncedLyrics),
        LyricsSyncOffsetMs);

    /// <summary>
    /// The provenance rule in one place, as a static so a projection that never materialises the entity
    /// (the songs list) reaches the same verdict as the entity itself.
    ///
    /// Note the asymmetry, which is deliberate: an AI transcription we could NOT align to the official
    /// lyrics is reported as fully AI-generated, because its words are the machine's guess. Anything else
    /// the AI touched only moved timestamps, and the words stayed human.
    /// </summary>
    public static LyricsProvenance ComputeLyricsProvenance(
        bool showingTranscription, bool alignedToReference, bool hasSyncedLyrics, int? syncOffsetMs)
        => showingTranscription
            ? (alignedToReference ? LyricsProvenance.AiEnhanced : LyricsProvenance.AiGenerated)
            : syncOffsetMs is not null && hasSyncedLyrics
                ? LyricsProvenance.AiEnhanced
                : LyricsProvenance.Human;

    // --- AI lyrics pronunciation + translation (generated on demand from the display lyrics) ---
    //
    // An LLM-generated romanization/pronunciation guide (Arabizi for Arabic, pinyin, romaji, phonetic
    // respelling for Latin-script languages) and English translation, kept line-aligned with the lyrics
    // they were generated from so the viewer can stack them under the original. Display-only: these
    // fields are deliberately NOT in RebuildOnMetadataChangeInterceptor.TagRelevantProperties and must
    // never re-tag the destination file.

    public string? RomanizedSyncedLyrics { get; set; }
    public string? RomanizedPlainLyrics { get; set; }
    public string? TranslatedSyncedLyrics { get; set; }
    public string? TranslatedPlainLyrics { get; set; }

    /// <summary>Dominant lyrics language detected during translation (ISO 639-1, e.g. "ar", "es", "en").</summary>
    public string? DetectedLyricsLanguage { get; set; }

    public LyricsTranslationStatus LyricsTranslationStatus { get; set; } = LyricsTranslationStatus.NotRequested;
    public DateTime? LyricsTranslatedAtUtc { get; set; }
    public string? LyricsTranslationModel { get; set; }
    public string? LyricsTranslationError { get; set; }

    /// <summary>
    /// Fingerprint (<see cref="ComputeLyricsFingerprint"/>) of the lyrics the translation was generated
    /// from. When the display lyrics later change (a re-transcription, a preferred-source flip), the
    /// stored translation describes the OLD text — <see cref="IsLyricsTranslationStale"/> flags that so
    /// the UI can regenerate instead of stacking mismatched lines.
    /// </summary>
    public string? LyricsTranslationSourceHash { get; set; }

    /// <summary>The lyrics a translation would be generated from right now (synced preferred).</summary>
    public string? CurrentLyricsForTranslation =>
        !string.IsNullOrWhiteSpace(DisplaySyncedLyrics) ? DisplaySyncedLyrics : DisplayPlainLyrics;

    /// <summary>True when a completed translation no longer matches the current display lyrics.</summary>
    public bool IsLyricsTranslationStale =>
        LyricsTranslationStatus == LyricsTranslationStatus.Completed
        && LyricsTranslationSourceHash is not null
        && ComputeLyricsFingerprint(CurrentLyricsForTranslation) != LyricsTranslationSourceHash;

    public static string? ComputeLyricsFingerprint(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
            return null;
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(lyrics.Trim()));
        return Convert.ToHexString(bytes);
    }

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
        && LibraryBuildStatus != LibraryBuildStatus.Done
        // Hold a fresh build until the lyrics fetch has resolved, so the file is tagged with lyrics in
        // one pass (enrichment commits Matched before the lyrics fetch returns). Tracks that can't be
        // searched for lyrics (no title/artist) aren't held. The builder additionally bounds this wait
        // by time (LyricsBeforeBuildWaitMinutes) so a never-fetched match — e.g. a manual approval —
        // still builds; that time relaxation lives in LibraryBuildQuery, not this pure predicate.
        && (LyricsStatus != LyricsStatus.NotFetched || !IsReadyForLyricsFetch);

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
    /// agree on one album: the unified album-artist, album title/year and the canonical track/disc
    /// number. Used when an album's tracks were each enriched against a different release/provider and
    /// so carry inconsistent album-artist spellings / years / track numbers; the canonical
    /// (multi-provider) tracklist is the source of truth. Reversible — captures originals first, so
    /// <see cref="ResetEnrichment"/> with <c>restoreOriginal</c> restores them. Deliberately does NOT
    /// touch <see cref="EnrichmentStatus"/>, <see cref="EnrichedAtUtc"/>, <see cref="MatchConfidence"/>
    /// or any grade, so it never triggers re-enrichment or an auto-regrade (grade staleness stays
    /// opt-in). Returns the field-level changes it made (empty when nothing changed) so the caller can
    /// record them in the change log.
    /// </summary>
    public IReadOnlyList<(string Field, string? OldValue, string? NewValue)> ApplyCanonicalCorrection(
        string? album, string? albumArtist, int? year, int? trackNumber, int? discNumber)
    {
        var changes = new List<(string, string?, string?)>();
        CaptureOriginalMetadata();

        if (!string.IsNullOrWhiteSpace(album) && !string.Equals(album, Album, StringComparison.Ordinal))
        {
            changes.Add((nameof(Album), Album, album));
            Album = album;
        }

        // Album-artist is an album-level field; a divergent per-track spelling (one provider's
        // "Lauryn Hill" vs another's "Ms. Lauryn Hill") splits the album across destination folders.
        if (!string.IsNullOrWhiteSpace(albumArtist) && !string.Equals(albumArtist, AlbumArtist, StringComparison.Ordinal))
        {
            changes.Add((nameof(AlbumArtist), AlbumArtist, albumArtist));
            AlbumArtist = albumArtist;
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
        if (!string.IsNullOrWhiteSpace(match.Genre)) Genre = match.Genre;
        if (!string.IsNullOrWhiteSpace(match.ReleaseDate)) ReleaseDate = match.ReleaseDate;
        if (!string.IsNullOrWhiteSpace(match.OriginalReleaseDate)) OriginalReleaseDate = match.OriginalReleaseDate;
        if (!string.IsNullOrWhiteSpace(match.Label)) Label = match.Label;
        if (!string.IsNullOrWhiteSpace(match.CatalogNumber)) CatalogNumber = match.CatalogNumber;
        if (!string.IsNullOrWhiteSpace(match.Upc)) Upc = match.Upc;
        if (!string.IsNullOrWhiteSpace(match.Composer)) Composer = match.Composer;
        if (!string.IsNullOrWhiteSpace(match.Copyright)) Copyright = match.Copyright;
        if (!string.IsNullOrWhiteSpace(match.ArtistSort)) ArtistSort = match.ArtistSort;
        if (!string.IsNullOrWhiteSpace(match.AlbumArtistSort)) AlbumArtistSort = match.AlbumArtistSort;
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

    /// <summary>
    /// Puts the song back to <see cref="EnrichmentStatus.Pending"/> and drops every enrichment-derived
    /// value, including the <see cref="ProviderAttempts"/> that decide which providers re-run.
    /// <para>
    /// <b>Callers must load the song with <c>.Include(s =&gt; s.ProviderAttempts)</c>.</b> On an unloaded
    /// navigation the <c>Clear()</c> below is a silent no-op: the attempt rows survive, the orchestrator
    /// skips every provider that already has a Matched attempt (and every NoMatch/Failed one still on
    /// cooldown), and the song can sit in Pending — invisible in the destination library — until its
    /// cooldowns elapse.
    /// </para>
    /// <para>
    /// Resetting only makes the song <i>eligible</i> for enrichment; it does not queue it. Callers
    /// outside the sweeps must also enqueue it on <see cref="Enrichment.EnrichmentPipelineChannel"/>.
    /// </para>
    /// </summary>
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

        // Descriptive metadata is enrichment-sourced (no captured original to restore), so clear it.
        Genre = null;
        ReleaseDate = null;
        OriginalReleaseDate = null;
        Label = null;
        CatalogNumber = null;
        Upc = null;
        Composer = null;
        Copyright = null;
        ArtistSort = null;
        AlbumArtistSort = null;

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
        // "Added to library" time — set once, on the FIRST successful build. A later in-place re-tag
        // (RequeueForRetag, which keeps this timestamp) must NOT bump it: album-identity heals and other
        // re-tags would otherwise resurface an old album at the top of every "recently added" view. A
        // genuine re-add (ResetLibraryBuild clears it) sets a fresh time here.
        LibraryBuiltAtUtc ??= DateTime.UtcNow;
        LibraryBuildError = null;
        LibraryBuildAttempts = 0;
        DestinationPath = destinationPath;
        PreviousDestinationPath = null;
    }

    public void MarkBuildFailed(string error)
    {
        LibraryBuildStatus = LibraryBuildStatus.Failed;
        LibraryBuildError = Truncate(error, MaxErrorLength);
        LibraryBuiltAtUtc = null;
        LibraryBuildLastAttemptedAtUtc = DateTime.UtcNow;
        LibraryBuildAttempts++;
    }

    public void ResetLibraryBuild()
    {
        LibraryBuildStatus = LibraryBuildStatus.Pending;
        LibraryBuiltAtUtc = null;
        LibraryBuildLastAttemptedAtUtc = null;
        LibraryBuildAttempts = 0;
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
        // Deliberately preserve LibraryBuiltAtUtc: a re-tag is the SAME track being rewritten in place,
        // not a new addition, so it keeps its original "added to library" time (MarkBuildDone won't
        // overwrite a non-null value). Only a real re-add via ResetLibraryBuild clears it.
        LibraryBuildLastAttemptedAtUtc = null;
        LibraryBuildAttempts = 0;
        LibraryBuildError = null;
        PreviousDestinationPath = DestinationPath;
    }

    /// <summary>
    /// Points this row at a different (better-quality) source file while preserving everything the
    /// row *means*: Id (streaming URLs are Id-addressed), enrichment identity/tags, lyrics, and the
    /// Original* snapshot all survive; only the file-identity facts and the fingerprint change. Used
    /// by the Soulseek quality-upgrade merge and the sync-receive replace path. The caller must
    /// follow with <see cref="ResetLibraryBuild"/> so the builder swaps the destination file (an
    /// extension change relocates <see cref="DestinationPath"/>; the old file is pruned via
    /// <see cref="PreviousDestinationPath"/>), and owns resolving the <c>(OwnerUserId, SourcePath)</c>
    /// unique-index handoff when another row occupied the new path. <see cref="HasCoverArt"/> is
    /// deliberately untouched — it's refreshed by the next scan / cover pipeline, and
    /// <see cref="AcquiredAtUtc"/> is pinned on the way in so the swap never moves the track in
    /// "recently added".
    /// </summary>
    public void ApplySourceUpgrade(
        string sourcePath,
        long fileSizeBytes,
        string fileName,
        string extension,
        DateTime lastModifiedUtc,
        int? bitrate,
        string? fingerprint,
        int? durationSeconds,
        int? durationMs)
    {
        // Pin the acquisition date before the upgrade destroys the evidence for it. This method
        // overwrites IndexedAtUtc and every caller then clears LibraryBuiltAtUtc — exactly the two
        // stamps the client falls back to while AcquiredAtUtc is null. Without this, a row old
        // enough to predate the column reads as "added today" the moment it is upgraded, which is
        // the failure mode AcquiredAtUtc exists to prevent.
        AcquiredAtUtc ??= SeedAcquiredAt(LastModifiedUtc, IndexedAtUtc);
        SourcePath = sourcePath;
        FileSizeBytes = fileSizeBytes;
        FileName = fileName;
        Extension = extension;
        LastModifiedUtc = lastModifiedUtc;
        Bitrate = bitrate;
        Fingerprint = fingerprint;
        DurationSeconds = durationSeconds ?? DurationSeconds;
        DurationMs = durationMs ?? DurationMs;
        IndexedAtUtc = DateTime.UtcNow;
        // The old file's write snapshot no longer describes the new source; drop it so the next
        // build diffs from scratch instead of a stale baseline.
        LastWrittenTagsJson = null;
        LastWrittenAtUtc = null;
    }

    public void ResetPostFingerprint()
    {
        ResetEnrichment(restoreOriginal: true);
        ResetLibraryBuild();
        PreviousDestinationPath = null;
        IsDuplicate = false;
        DuplicateOfId = null;
        DuplicateKeeperPinnedAtUtc = null;
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

    public void ApplyLyricsResult(
        string? syncedLyrics, string? plainLyrics, bool instrumental, int? lrclibId = null, double? lrclibDuration = null)
    {
        IsInstrumental = instrumental;
        if (lrclibId is not null) LrclibId = lrclibId.Value.ToString();
        if (lrclibDuration is > 0) LrclibDurationSeconds = lrclibDuration;
        // New text means any earlier timing verdict (and any offset we applied to the old text) is void.
        ResetLyricsSyncCheck();
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

    // --- Lyrics re-check (LRCLIB grows over time) ---

    /// <summary>
    /// True when a *later* LRCLIB query could still improve this song's lyrics: it 404'd, the fetch
    /// errored, or it returned unsynced lyrics only and an LRC may have been contributed since.
    /// Songs confirmed instrumental, songs that already have synced lyrics, and songs still at
    /// <see cref="LyricsStatus.NotFetched"/> (owned by the backfill sweep, which fetches immediately
    /// rather than after a cooldown) are deliberately excluded.
    /// </summary>
    public bool IsLyricsRecheckCandidate =>
        !IsDeleted
        && (EnrichmentStatus == EnrichmentStatus.Matched || EnrichmentStatus == EnrichmentStatus.NeedsReview)
        && !string.IsNullOrWhiteSpace(Title)
        && !string.IsNullOrWhiteSpace(Artist)
        && (LyricsStatus == LyricsStatus.NotFound
            || LyricsStatus == LyricsStatus.Failed
            || (LyricsStatus == LyricsStatus.Fetched && string.IsNullOrWhiteSpace(SyncedLyrics)));

    /// <summary>
    /// Records a completed LRCLIB attempt and schedules the next one. The delay doubles per attempt
    /// (<see cref="ComputeLyricsRecheckDelayDays"/>) so a track LRCLIB genuinely doesn't carry is asked
    /// about ever more rarely instead of forever on a fixed interval — LRCLIB is a free community
    /// service. Clears the schedule outright once the outcome is terminal (synced lyrics or
    /// instrumental), so a resolved song never queues again.
    /// </summary>
    public void RecordLyricsAttempt(DateTime nowUtc, int baseCooldownDays, int maxCooldownDays)
    {
        LyricsLastAttemptedAtUtc = nowUtc;
        LyricsFetchAttempts++;
        LyricsNextRecheckAfterUtc = IsLyricsRecheckCandidate
            ? nowUtc.AddDays(ComputeLyricsRecheckDelayDays(LyricsFetchAttempts, baseCooldownDays, maxCooldownDays))
            : null;
    }

    /// <summary>
    /// Exponential backoff for lyrics re-checks: <c>base * 2^(attempts-1)</c>, capped at <paramref name="maxCooldownDays"/>.
    /// The shift is bounded before it is applied so a long-lived song can't overflow the exponent.
    /// </summary>
    public static int ComputeLyricsRecheckDelayDays(int attempts, int baseCooldownDays, int maxCooldownDays)
    {
        if (maxCooldownDays < baseCooldownDays)
            maxCooldownDays = baseCooldownDays;

        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var delay = (long)baseCooldownDays << exponent;
        return (int)Math.Min(delay, maxCooldownDays);
    }

    /// <summary>
    /// Applies an LRCLIB re-check result, but only when it is strictly better than what is already
    /// stored — returning true when it was applied. A re-check must never *lose* lyrics: LRCLIB search
    /// results shift over time, so a later query returning nothing (or a bare instrumental flag) for a
    /// song we already have plain lyrics for is treated as noise and ignored rather than clearing them.
    /// Anything that would leave the song unchanged returns false so the caller skips the re-tag.
    /// </summary>
    public bool TryApplyLyricsUpgrade(
        string? syncedLyrics, string? plainLyrics, bool instrumental, int? lrclibId = null, double? lrclibDuration = null)
    {
        // Already the best outcome LRCLIB can give us — nothing to upgrade.
        if (!string.IsNullOrWhiteSpace(SyncedLyrics))
            return false;

        var hasPlain = !string.IsNullOrWhiteSpace(PlainLyrics);

        if (instrumental)
        {
            // Only trust an instrumental verdict when we're not holding real lyrics for the track.
            if (hasPlain)
                return false;

            ApplyLyricsResult(null, null, true, lrclibId, lrclibDuration);
            return true;
        }

        var gainedSynced = !string.IsNullOrWhiteSpace(syncedLyrics);
        var gainedPlain = !hasPlain && !string.IsNullOrWhiteSpace(plainLyrics);
        if (!gainedSynced && !gainedPlain)
            return false;

        // Keep the plain lyrics we already have if this response carried only the synced form.
        ApplyLyricsResult(syncedLyrics, plainLyrics ?? PlainLyrics, false, lrclibId, lrclibDuration);
        return true;
    }

    public void ResetLyrics()
    {
        LyricsStatus = LyricsStatus.NotFetched;
        SyncedLyrics = null;
        PlainLyrics = null;
        IsInstrumental = null;
        LrclibId = null;
        // Back to NotFetched means the backfill sweep fetches immediately, so the re-check backoff
        // starts from scratch rather than inheriting the old song's cooldown.
        LyricsLastAttemptedAtUtc = null;
        LyricsFetchAttempts = 0;
        LyricsNextRecheckAfterUtc = null;
        LrclibDurationSeconds = null;
        ResetLyricsSyncCheck();
        // The pronunciation/translation was generated from these lyrics — resetting them makes it stale.
        ResetLyricsTranslation();
    }

    // --- AI transcription lifecycle ---

    /// <param name="alignedToReference">
    /// True when these lines are the song's official words re-timed against the audio rather than the AI's
    /// own guess at the words — the bit <see cref="LyricsProvenance"/> turns into an "AI enhanced" vs
    /// "AI generated" label.
    /// </param>
    public void ApplyTranscriptionResult(
        string? syncedLyrics, string? plainLyrics, string model, bool alignedToReference = false)
    {
        TranscribedSyncedLyrics = string.IsNullOrWhiteSpace(syncedLyrics) ? null : syncedLyrics;
        TranscribedPlainLyrics = string.IsNullOrWhiteSpace(plainLyrics) ? null : plainLyrics;
        TranscriptionStatus = TranscriptionStatus.Completed;
        TranscribedAtUtc = DateTime.UtcNow;
        TranscriptionModel = model;
        TranscriptionError = null;
        TranscriptionAlignedToReference = alignedToReference;
    }

    public void MarkTranscriptionFailed(string error)
    {
        TranscriptionStatus = TranscriptionStatus.Failed;
        TranscriptionError = Truncate(error, MaxErrorLength);
        TranscribedAtUtc = DateTime.UtcNow;
    }

    public void ResetTranscription()
    {
        TranscriptionStatus = TranscriptionStatus.NotRequested;
        TranscribedSyncedLyrics = null;
        TranscribedPlainLyrics = null;
        TranscribedAtUtc = null;
        TranscriptionModel = null;
        TranscriptionError = null;
        TranscriptionAlignedToReference = false;
    }

    // --- Lyrics timing validation lifecycle ---

    /// <summary>Clears every timing verdict, including a measured offset. Call whenever the LRC text changes.</summary>
    public void ResetLyricsSyncCheck()
    {
        LyricsSyncStatus = LyricsSyncStatus.NotChecked;
        LyricsSyncIssue = null;
        LyricsSyncCheckedAtUtc = null;
        LyricsSyncOffsetMs = null;
        LyricsSyncConfidence = null;
        LyricsSyncProbeAttempts = 0;
    }

    /// <summary>Records a verdict from the free (arithmetic) checks or from the AI probe.</summary>
    public void ApplyLyricsSyncVerdict(LyricsSyncStatus status, string? issue, double? confidence = null)
    {
        LyricsSyncStatus = status;
        LyricsSyncIssue = status is LyricsSyncStatus.Ok or LyricsSyncStatus.Corrected || issue is null
            ? null
            : Truncate(issue, MaxErrorLength);
        LyricsSyncCheckedAtUtc = DateTime.UtcNow;
        if (confidence is not null)
            LyricsSyncConfidence = confidence;
    }

    /// <summary>
    /// Replaces <see cref="SyncedLyrics"/> with the same lines shifted by a measured constant offset. The
    /// words are untouched — only the timestamps move — so the result stays the human-written lyric with
    /// AI-derived timing, which <see cref="LyricsProvenance"/> reports as <see cref="LyricsProvenance.AiEnhanced"/>.
    /// </summary>
    public void ApplyLyricsSyncOffset(string shiftedLrc, int offsetMs, double? confidence)
    {
        SyncedLyrics = string.IsNullOrWhiteSpace(shiftedLrc) ? SyncedLyrics : shiftedLrc;
        // Accumulate: a second probe measures against the ALREADY-shifted text, so the total drift from the
        // LRCLIB original is the sum, and that total is what the label reasons about.
        LyricsSyncOffsetMs = (LyricsSyncOffsetMs ?? 0) + offsetMs;
        ApplyLyricsSyncVerdict(LyricsSyncStatus.Corrected, null, confidence);
    }

    /// <summary>Notes that the paid AI probe ran, whatever it concluded, so a hopeless row stops being retried.</summary>
    public void RecordLyricsSyncProbeAttempt() => LyricsSyncProbeAttempts++;

    // --- AI pronunciation/translation lifecycle ---

    /// <summary>
    /// Stores the LLM-generated pronunciation + translation. All-null lyrics with an "en" language code
    /// is a valid Completed outcome: the song was already English, so there was nothing to generate.
    /// </summary>
    public void ApplyLyricsTranslationResult(
        string? romanizedSynced,
        string? romanizedPlain,
        string? translatedSynced,
        string? translatedPlain,
        string? languageCode,
        string model,
        string? sourceHash = null)
    {
        RomanizedSyncedLyrics = string.IsNullOrWhiteSpace(romanizedSynced) ? null : romanizedSynced;
        RomanizedPlainLyrics = string.IsNullOrWhiteSpace(romanizedPlain) ? null : romanizedPlain;
        TranslatedSyncedLyrics = string.IsNullOrWhiteSpace(translatedSynced) ? null : translatedSynced;
        TranslatedPlainLyrics = string.IsNullOrWhiteSpace(translatedPlain) ? null : translatedPlain;
        DetectedLyricsLanguage = string.IsNullOrWhiteSpace(languageCode) ? null : languageCode;
        LyricsTranslationStatus = LyricsTranslationStatus.Completed;
        LyricsTranslatedAtUtc = DateTime.UtcNow;
        LyricsTranslationModel = model;
        LyricsTranslationError = null;
        LyricsTranslationSourceHash = sourceHash;
    }

    public void MarkLyricsTranslationFailed(string error)
    {
        LyricsTranslationStatus = LyricsTranslationStatus.Failed;
        LyricsTranslationError = Truncate(error, MaxErrorLength);
        LyricsTranslatedAtUtc = DateTime.UtcNow;
    }

    public void ResetLyricsTranslation()
    {
        LyricsTranslationStatus = LyricsTranslationStatus.NotRequested;
        RomanizedSyncedLyrics = null;
        RomanizedPlainLyrics = null;
        TranslatedSyncedLyrics = null;
        TranslatedPlainLyrics = null;
        DetectedLyricsLanguage = null;
        LyricsTranslatedAtUtc = null;
        LyricsTranslationModel = null;
        LyricsTranslationError = null;
        LyricsTranslationSourceHash = null;
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

public enum TranscriptionStatus
{
    NotRequested = 0,
    Pending = 1,
    Completed = 2,
    Failed = 3,
}

public enum PreferredLyricsSource
{
    Lrclib = 0,
    Transcribed = 1,
}

/// <summary>Outcome of checking a stored LRC's timestamps against the actual audio. DB contract — never renumber.</summary>
public enum LyricsSyncStatus
{
    /// <summary>No verdict yet (no synced lyrics, or the check has not run).</summary>
    NotChecked = 0,

    /// <summary>The LRC's timing is consistent with the track. Either the free checks passed or the AI probe agreed.</summary>
    Ok = 1,

    /// <summary>The timing is inconsistent with the track — most often an LRC belonging to a different recording.</summary>
    Suspect = 2,

    /// <summary>The AI probe found a constant offset and shifted every line by it; the timing is now trusted.</summary>
    Corrected = 3,

    /// <summary>Checked, but nothing conclusive could be said (too few lines, unknown track duration).</summary>
    Unverifiable = 4,
}

/// <summary>How much of the lyrics the app is currently displaying came from an AI. DB contract — never renumber.</summary>
public enum LyricsProvenance
{
    /// <summary>Human-contributed LRCLIB lyrics, timing untouched.</summary>
    Human = 0,

    /// <summary>The song's official words, re-timed by AI — a forced alignment or a measured constant offset.</summary>
    AiEnhanced = 1,

    /// <summary>Both the words and the timing came from an AI transcription of the audio.</summary>
    AiGenerated = 2,
}

public enum LyricsTranslationStatus
{
    NotRequested = 0,
    Pending = 1,
    Completed = 2,
    Failed = 3,
}
