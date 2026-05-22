using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

public class MusicEnricherOptions
{
    public const string SectionName = "MusicEnricher";

    [Required(ErrorMessage = "MusicEnricher:SourceDirectory is required.")]
    public string SourceDirectory { get; set; } = string.Empty;

    [Required(ErrorMessage = "MusicEnricher:DestinationDirectory is required.")]
    public string DestinationDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Master switch for the automatic <em>processing</em> cascade: the scan→fingerprint→enrich→build
    /// auto-triggering, the fingerprint/build auto-poll loops, and the enrichment startup backfill +
    /// periodic retry sweep. The discovery scan (file indexing) is intentionally <em>not</em> gated —
    /// it runs on startup/reconnect regardless, so the library always populates and there's something
    /// to test against. When false, no processing stage starts on its own; the user drives each step
    /// via the manual trigger endpoints (and the always-running enrichment workers still process
    /// anything explicitly enqueued, e.g. a manual fingerprint or per-song / per-folder enrich).
    /// Defaults to true; set to false in resource-constrained preview environments via
    /// <c>MusicEnricher__AutoStartPipeline=false</c>.
    /// </summary>
    public bool AutoStartPipeline { get; set; } = true;

    /// <summary>
    /// How often (seconds) to probe whether the source/destination directories are
    /// reachable. Pipeline stages that touch the filesystem gate on the cached result,
    /// so an unreachable network share (e.g. laptop away from home) degrades gracefully
    /// instead of spamming errors.
    /// </summary>
    [Range(5, 3600)]
    public int DirectoryProbeIntervalSeconds { get; set; } = 30;

    /// <summary>Per-directory timeout (seconds) for a reachability probe, so a hung SMB mount can't block the monitor.</summary>
    [Range(1, 120)]
    public int DirectoryProbeTimeoutSeconds { get; set; } = 5;

    /// <summary>Maximum concurrent file reads (tag reading + fpcalc) for SMB safety.</summary>
    [Range(1, 64)]
    public int SmbConcurrency { get; set; } = 8;

    /// <summary>Number of records accumulated before a DB write is flushed.</summary>
    [Range(1, 10000)]
    public int DbBatchSize { get; set; } = 100;

    /// <summary>Path to the fpcalc binary (Chromaprint). Must be on PATH or an absolute path.</summary>
    public string FpcalcPath { get; set; } = "fpcalc";

    /// <summary>Number of tracks processed per fingerprint batch.</summary>
    [Range(1, 10000)]
    public int FingerprintBatchSize { get; set; } = 200;

    /// <summary>Number of concurrent fpcalc workers during fingerprinting.</summary>
    [Range(1, 64)]
    public int FingerprintConcurrency { get; set; } = 8;

    /// <summary>Delay in seconds before retrying when no tracks are pending fingerprinting.</summary>
    [Range(1, 300)]
    public int FingerprintIdleDelaySeconds { get; set; } = 10;

    /// <summary>AcoustID application API key for fingerprint lookups.</summary>
    public string AcoustIdApiKey { get; set; } = string.Empty;

    /// <summary>Minimum AcoustID match score (0.0–1.0) to accept a result.</summary>
    [Range(0.0, 1.0)]
    public double AcoustIdScoreThreshold { get; set; } = 0.85;

    /// <summary>Max AcoustID requests per second across the whole process.</summary>
    [Range(1, 20)]
    public int AcoustIdRequestsPerSecond { get; set; } = 3;

    /// <summary>Number of tracks processed per enrichment cycle.</summary>
    [Range(1, 10000)]
    public int EnrichmentBatchSize { get; set; } = 200;

    /// <summary>Number of concurrent enrichment workers per cycle.</summary>
    [Range(1, 64)]
    public int EnrichmentWorkerConcurrency { get; set; } = 2;

    /// <summary>Delay in seconds before retrying when no tracks are pending enrichment.</summary>
    [Range(1, 300)]
    public int EnrichmentIdleDelaySeconds { get; set; } = 15;

    /// <summary>Max concurrent songs calling the AcoustID provider simultaneously.</summary>
    [Range(1, 20)]
    public int AcoustIdConcurrency { get; set; } = 3;

    /// <summary>Max concurrent songs calling the Spotify API provider simultaneously.</summary>
    [Range(1, 20)]
    public int SpotifyApiConcurrency { get; set; } = 1;

    /// <summary>Interval in seconds for the periodic sweep that re-enqueues rate-limited songs.</summary>
    [Range(30, 1800)]
    public int EnrichmentRetrySweepIntervalSeconds { get; set; } = 300;

    /// <summary>On API startup, reset every NeedsReview song to Pending (clears its provider attempts) and re-queue it.</summary>
    public bool RetryNeedsReviewOnStartup { get; set; } = false;

    /// <summary>On API startup, reset every Failed song to Pending (clears its provider attempts) and re-queue it.</summary>
    public bool RetryFailedOnStartup { get; set; } = false;

    /// <summary>Enable the AcoustID enrichment provider (fingerprint → MusicBrainz via AcoustID).</summary>
    public bool EnableAcoustIdProvider { get; set; } = true;

    /// <summary>
    /// Enable the MusicBrainz web-service enrichment provider (MBID / ISRC / artist+title search).
    /// On by default: it's a second name-based voter that enables genuine multi-provider consensus
    /// and corroborates AcoustID fingerprint hits via the recording MBID.
    /// </summary>
    public bool EnableMusicBrainzWebProvider { get; set; } = true;

    /// <summary>Enable the Spotify API enrichment provider (artist+title, optional ISRC verification).</summary>
    public bool EnableSpotifyApiProvider { get; set; } = false;

    /// <summary>Enable the community tracker enrichment provider (unreleased/leak files).</summary>
    public bool EnableTrackerProvider { get; set; } = false;

    /// <summary>Max Spotify Web API search requests per second (catalog enrichment, client-credentials).</summary>
    [Range(1, 20)]
    public int SpotifyApiRequestsPerSecond { get; set; } = 4;

    /// <summary>Track candidates to request from Spotify search (1–50).</summary>
    [Range(1, 50)]
    public int SpotifyApiSearchLimit { get; set; } = 10;

    /// <summary>How long to cache Spotify search responses in memory.</summary>
    [Range(1, 1440)]
    public int SpotifyApiSearchCacheMinutes { get; set; } = 30;

    /// <summary>Minimum adjusted confidence to return any result from Spotify provider (&lt; this → no match).</summary>
    [Range(0.0, 1.0)]
    public double SpotifyApiMinConfidence { get; set; } = 0.7;

    /// <summary>Minimum adjusted confidence for <see cref="EnrichmentStatus.Matched"/> (otherwise NeedsReview).</summary>
    [Range(0.0, 1.0)]
    public double SpotifyApiMatchedThreshold { get; set; } = 0.85;

    /// <summary>Additive boost (0–0.3) applied when file ISRC matches Spotify track external ISRC.</summary>
    [Range(0.0, 0.3)]
    public double SpotifyApiIsrcConfidenceBoost { get; set; } = 0.12;

    /// <summary>Multiply confidence when duration differs by more than <see cref="SpotifyApiDurationDeltaThresholdSeconds"/>.</summary>
    [Range(0.1, 1.0)]
    public double SpotifyApiDurationMismatchPenalty { get; set; } = 0.7;

    /// <summary>Duration delta (seconds) above which <see cref="SpotifyApiDurationMismatchPenalty"/> applies.</summary>
    [Range(1, 120)]
    public int SpotifyApiDurationDeltaThresholdSeconds { get; set; } = 20;

    /// <summary>Optional ISO 3166-1 alpha-2 market for Spotify search (empty = omit).</summary>
    public string SpotifyApiMarket { get; set; } = "";

    // --- Deezer (free, no-auth) ---

    /// <summary>Enable the Deezer enrichment provider (ISRC-first, then artist+title search).</summary>
    public bool EnableDeezerProvider { get; set; } = true;

    /// <summary>Max concurrent songs calling the Deezer provider simultaneously.</summary>
    [Range(1, 20)]
    public int DeezerConcurrency { get; set; } = 1;

    /// <summary>Max Deezer API requests per second (Deezer allows ~50 req / 5s).</summary>
    [Range(1, 20)]
    public int DeezerApiRequestsPerSecond { get; set; } = 4;

    /// <summary>Track candidates to request from Deezer search (1–50).</summary>
    [Range(1, 50)]
    public int DeezerApiSearchLimit { get; set; } = 10;

    /// <summary>How long to cache Deezer search / track responses in memory.</summary>
    [Range(1, 1440)]
    public int DeezerApiSearchCacheMinutes { get; set; } = 30;

    /// <summary>Minimum adjusted confidence to return any result from Deezer (&lt; this → no match).</summary>
    [Range(0.0, 1.0)]
    public double DeezerApiMinConfidence { get; set; } = 0.7;

    /// <summary>Minimum adjusted confidence for Deezer to recommend <see cref="EnrichmentStatus.Matched"/>.</summary>
    [Range(0.0, 1.0)]
    public double DeezerApiMatchedThreshold { get; set; } = 0.85;

    /// <summary>Duration delta (seconds) above which the Deezer duration-mismatch penalty applies.</summary>
    [Range(1, 120)]
    public int DeezerApiDurationDeltaThresholdSeconds { get; set; } = 20;

    // --- Apple / iTunes (free, no-auth) ---

    /// <summary>Enable the Apple/iTunes enrichment provider (artist+title search; no ISRC).</summary>
    public bool EnableAppleMusicProvider { get; set; } = true;

    /// <summary>Max concurrent songs calling the Apple Music provider simultaneously.</summary>
    [Range(1, 20)]
    public int AppleMusicConcurrency { get; set; } = 1;

    /// <summary>Max iTunes Search API requests per second. iTunes is throttled to ~20 req/min, so keep this at 1.</summary>
    [Range(1, 20)]
    public int AppleMusicApiRequestsPerSecond { get; set; } = 1;

    /// <summary>Track candidates to request from iTunes search (1–50).</summary>
    [Range(1, 50)]
    public int AppleMusicApiSearchLimit { get; set; } = 10;

    /// <summary>How long to cache iTunes search responses in memory.</summary>
    [Range(1, 1440)]
    public int AppleMusicApiSearchCacheMinutes { get; set; } = 30;

    /// <summary>Minimum adjusted confidence to return any result from Apple Music (&lt; this → no match).</summary>
    [Range(0.0, 1.0)]
    public double AppleMusicApiMinConfidence { get; set; } = 0.7;

    /// <summary>Minimum adjusted confidence for Apple Music to recommend <see cref="EnrichmentStatus.Matched"/>.</summary>
    [Range(0.0, 1.0)]
    public double AppleMusicApiMatchedThreshold { get; set; } = 0.85;

    /// <summary>Duration delta (seconds) above which the Apple Music duration-mismatch penalty applies.</summary>
    [Range(1, 120)]
    public int AppleMusicApiDurationDeltaThresholdSeconds { get; set; } = 20;

    /// <summary>ISO 3166-1 alpha-2 storefront country for the iTunes Search API.</summary>
    public string AppleMusicCountry { get; set; } = "US";

    // --- MusicBrainz web service ---

    /// <summary>User-Agent sent to MusicBrainz (required by their policy). Include contact info.</summary>
    public string MusicBrainzUserAgent { get; set; } = "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)";

    /// <summary>MusicBrainz rate limit (their policy is 1 request/second per app).</summary>
    [Range(1, 10)]
    public int MusicBrainzRequestsPerSecond { get; set; } = 1;

    /// <summary>Days before a terminal NoMatch provider attempt is retried (catalogs grow). 0 = never.</summary>
    [Range(0, 3650)]
    public int EnrichmentNoMatchRetryDays { get; set; } = 30;

    /// <summary>Days before a terminal Failed provider attempt is retried (failures are often transient). 0 = never.</summary>
    [Range(0, 3650)]
    public int EnrichmentFailedRetryDays { get; set; } = 7;

    /// <summary>Minimum confidence to return any result from the MusicBrainz provider.</summary>
    [Range(0.0, 1.0)]
    public double MusicBrainzMinConfidence { get; set; } = 0.7;

    /// <summary>Minimum confidence for the MusicBrainz provider to recommend Matched.</summary>
    [Range(0.0, 1.0)]
    public double MusicBrainzMatchedThreshold { get; set; } = 0.85;

    // --- Community tracker (unreleased / leak files) ---

    /// <summary>Base URL of the community tracker REST API (Django REST; must end with a slash).</summary>
    public string TrackerApiBaseUrl { get; set; } = "https://juicewrldapi.com/juicewrld/";

    /// <summary>
    /// Artist names/aliases this tracker covers. The provider only attempts a lookup when the
    /// song's resolved artist fuzzy-matches one of these — the DB is single-artist, so this keeps
    /// it from wasting calls or mis-tagging unrelated music.
    /// </summary>
    public string[] TrackerArtistAllowlist { get; set; } = ["Juice WRLD", "Juice Wrld", "JuiceWRLD", "JuiceTheKidd"];

    /// <summary>Max tracker search candidates to request per song.</summary>
    [Range(1, 100)]
    public int TrackerSearchLimit { get; set; } = 20;

    /// <summary>Minimum adjusted confidence to return any result from the tracker provider (&lt; this → no match).</summary>
    [Range(0.0, 1.0)]
    public double TrackerMinConfidence { get; set; } = 0.7;

    /// <summary>Minimum adjusted confidence for the tracker provider to recommend Matched (otherwise NeedsReview).</summary>
    [Range(0.0, 1.0)]
    public double TrackerMatchedThreshold { get; set; } = 0.85;

    // --- Consensus / identity matching ---

    /// <summary>Minimum own-confidence for a provider candidate to act as a corroborating vote.</summary>
    [Range(0.0, 1.0)]
    public double ConsensusCorroborationFloor { get; set; } = 0.5;

    /// <summary>Fuzzy ratio (0–100) above which two candidate artist names are considered the same.</summary>
    [Range(0, 100)]
    public double IdentityArtistThreshold { get; set; } = 85;

    /// <summary>Fuzzy ratio (0–100) above which two candidate titles are considered the same.</summary>
    [Range(0, 100)]
    public double IdentityTitleThreshold { get; set; } = 85;

    /// <summary>Max duration delta (seconds) for two candidates to be considered the same recording.</summary>
    [Range(0, 120)]
    public double IdentityDurationDeltaSeconds { get; set; } = 8;

    /// <summary>
    /// Minimum consensus confidence (with ≥2 agreeing providers) required to auto-overwrite a
    /// *good* existing curated value. Below this, a conflicting change is proposed for review
    /// rather than applied — so a curated library is never silently degraded.
    /// </summary>
    [Range(0.0, 1.0)]
    public double AutoUpgradeConfidence { get; set; } = 0.96;

    /// <summary>Number of tracks processed per library-build cycle.</summary>
    [Range(1, 10000)]
    public int LibraryBuilderBatchSize { get; set; } = 100;

    /// <summary>Number of concurrent copy/tag workers per library-build cycle.</summary>
    [Range(1, 64)]
    public int LibraryBuilderWorkerConcurrency { get; set; } = 2;

    /// <summary>Delay in seconds before retrying when no tracks are pending library build.</summary>
    [Range(1, 300)]
    public int LibraryBuilderIdleDelaySeconds { get; set; } = 20;

    /// <summary>
    /// Top-level folder name compilations (Various-Artists releases) are filed under, keyed by
    /// album rather than per-track artist so the album stays together. Empty falls back to
    /// "Various Artists" — the literal album-artist string every music server recognizes.
    /// </summary>
    public string CompilationFolderName { get; set; } = "Various Artists";
}