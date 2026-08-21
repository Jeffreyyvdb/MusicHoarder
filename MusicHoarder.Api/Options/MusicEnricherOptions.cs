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
    /// Optional directory of real audio files used to seed the public demo account with playable
    /// songs (the owner curates a few albums into it). Only set on the hosted demo deployment via
    /// <c>MusicEnricher__DemoMediaDirectory</c> (a read-only bind mount) — it is intentionally unset
    /// for self-hosters and PR previews, who keep the synthetic demo seed instead. When set and the
    /// directory exists, <see cref="Auth.DemoSeederHostedService"/> ingests its files into the demo
    /// user as <c>Matched</c> + <c>Done</c> rows; when unset/missing, real seeding is skipped.
    /// </summary>
    public string? DemoMediaDirectory { get; set; }

    /// <summary>
    /// Optional override for the pipeline version stamped onto performance snapshots. When unset the
    /// snapshot falls back to the assembly informational version (then "dev"). Deploys can inject the
    /// released <c>vX.Y.Z</c> via <c>MusicEnricher__PipelineVersion</c> so timeline points carry the
    /// real semver; during local iteration the snapshot's config fingerprint is the version signal.
    /// </summary>
    public string? PipelineVersion { get; set; }

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
    /// Minutes between automatic re-scans of the source library, so files dropped onto the share are
    /// picked up without anyone clicking Scan. Without this a scan only fires on startup, on a source
    /// offline→online edge, after a download, or manually — so a copy into an already-online library
    /// stays invisible indefinitely. A re-scan that finds nothing is cheap: <c>IndexService</c> reads
    /// no tags and writes nothing when every file matches on mtime+size. 0 disables the timer.
    /// </summary>
    [Range(0, 10080)]
    public int AutoScanIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// How long (seconds) a file must have been untouched before a scan will index it. Guards against
    /// indexing a file that is still being copied in — a half-written file reads as corrupt tags and
    /// burns an enrichment attempt (which carries a provider cooldown) before the next scan re-processes
    /// it. Applies to every scan, manual ones included. 0 disables the guard.
    /// </summary>
    [Range(0, 3600)]
    public int ScanSettleSeconds { get; set; } = 60;

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

    /// <summary>
    /// How often (seconds) the <c>IngestRunMonitor</c> polls job state to open/refresh/finalize the
    /// ingest-run ledger. Short so a run's start/end edges are captured promptly; it only runs trivial
    /// indexed COUNT queries when idle. Default 3.
    /// </summary>
    [Range(1, 60)]
    public int IngestRunMonitorPollSeconds { get; set; } = 3;

    /// <summary>
    /// Enables the periodic GitHub Releases check that powers the in-app "update available" banner.
    /// Safe to disable in air-gapped/offline deploys via <c>MusicEnricher__EnableUpdateCheck=false</c>;
    /// when false the latest-version endpoint reports no update and GitHub is never contacted.
    /// </summary>
    public bool EnableUpdateCheck { get; set; } = true;

    /// <summary><c>owner/repo</c> slug polled for the latest release.</summary>
    public string UpdateCheckRepo { get; set; } = "Jeffreyyvdb/MusicHoarder";

    /// <summary>
    /// How often (hours) to poll GitHub for the latest release. Long by design — the result rarely
    /// changes and a long interval keeps the process well under GitHub's 60/hr unauthenticated limit.
    /// </summary>
    [Range(1, 168)]
    public int UpdateCheckIntervalHours { get; set; } = 8;

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

    /// <summary>Max AcoustID requests per second across the whole process.</summary>
    [Range(1, 20)]
    public int AcoustIdRequestsPerSecond { get; set; } = 3;

    /// <summary>
    /// Number of songs enrichment processes concurrently. Each worker pins one DbContext/connection for
    /// a song's whole multi-second external-I/O span, so this is the pipeline's throughput floor on a
    /// backlog. The hard external caps live downstream (per-provider semaphores + process-wide token
    /// buckets), so raising this only queues at those gates — it never exceeds any configured rps.
    /// Default 6 (returns flatten past ~5–6 in-flight; MusicBrainz at 1 rps bounds MB-dependent songs).
    /// </summary>
    [Range(1, 64)]
    public int EnrichmentWorkerConcurrency { get; set; } = 6;

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

    /// <summary>Enable the Spotify API enrichment provider (artist+title, optional ISRC verification). Default on (matches appsettings).</summary>
    public bool EnableSpotifyApiProvider { get; set; } = true;

    /// <summary>
    /// Enable the community tracker enrichment provider (unreleased/leak files). On by default:
    /// it's gated to <see cref="TrackerArtistAllowlist"/> so it only fires for the artists it
    /// covers (Juice WRLD), and for those songs its catalog of leaks / alternate versions is
    /// richer than the mainstream services.
    /// </summary>
    public bool EnableTrackerProvider { get; set; } = true;

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

    /// <summary>How long to cache Deezer discover browse responses (genres, chart / search playlists,
    /// playlist metadata + tracklists) in memory.</summary>
    [Range(1, 1440)]
    public int DeezerBrowseCacheMinutes { get; set; } = 10;

    /// <summary>Default number of discover playlists to return from chart / search browse.</summary>
    [Range(1, 100)]
    public int DeezerBrowsePlaylistLimit { get; set; } = 30;

    /// <summary>Max tracks fetched and returned for a single discover playlist detail view. The playlist's
    /// real total is still reported in <c>trackCount</c>; this only bounds the tracklist paged in the
    /// request path so a huge editorial playlist can't page thousands of tracks for a browse preview.</summary>
    [Range(1, 10000)]
    public int DiscoverDetailTrackLimit { get; set; } = 200;

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

    /// <summary>Max iTunes Search API requests per minute. Apple documents ~20/min (approx,
    /// subject to change), so stay conservatively under it.</summary>
    [Range(1, 30)]
    public int AppleMusicApiRequestsPerMinute { get; set; } = 15;

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

    // --- Canonical album tracklists (multi-provider, reconciled; full-album view) ---

    /// <summary>
    /// Enable the background sweep that fetches each album's full tracklist from every enabled provider
    /// (MusicBrainz, Spotify, Deezer, Apple) and reconciles them, so the album view can show every real
    /// track and grey out the ones the user is missing.
    /// </summary>
    public bool EnableCanonicalAlbumFetch { get; set; } = true;

    /// <summary>Number of albums fetched+reconciled per canonical-album sweep cycle.</summary>
    [Range(1, 1000)]
    public int CanonicalAlbumFetchBatchSize { get; set; } = 10;

    /// <summary>Delay in seconds before the canonical-album sweep re-checks for new albums to fetch.</summary>
    [Range(5, 3600)]
    public int CanonicalAlbumFetchIdleDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Upper bound (seconds) the canonical-album idle delay backs off to while there's nothing to fetch.
    /// The sweep materializes every matched song each cycle, so an empty run shouldn't repeat every 30s
    /// forever; the delay doubles from <see cref="CanonicalAlbumFetchIdleDelaySeconds"/> up to this ceiling
    /// and resets the moment a sweep finds work. Kept well under <see cref="CanonicalAlbumFailedRetryMinutes"/>
    /// so timer-driven NotFound/Failed retries are never starved. Default 300 (5 min).
    /// </summary>
    [Range(5, 3600)]
    public int CanonicalAlbumFetchMaxIdleDelaySeconds { get; set; } = 300;

    /// <summary>Days before a NotFound canonical-album fetch is retried (catalogs grow). 0 = never.</summary>
    [Range(0, 3650)]
    public int CanonicalAlbumNotFoundRetryDays { get; set; } = 30;

    /// <summary>Minutes to back off before retrying a transiently-failed canonical-album fetch.</summary>
    [Range(1, 10080)]
    public int CanonicalAlbumFailedRetryMinutes { get; set; } = 60;

    // --- External cover art (destination folder covers) ---

    /// <summary>
    /// Fetch a front cover from external providers (Cover Art Archive → Deezer → iTunes) when an
    /// album's source files carry no art, and write it as the destination folder's <c>cover.&lt;ext&gt;</c>.
    /// Master switch for both the build-time fallback and the periodic back-catalog sweep.
    /// </summary>
    public bool EnableExternalCoverArtFetch { get; set; } = true;

    /// <summary>Try the Cover Art Archive (keyed by MusicBrainz release / release-group MBID) first.</summary>
    public bool EnableCoverArtArchiveCovers { get; set; } = true;

    /// <summary>
    /// Fetch the official Spotify album image (keyed by the matched Spotify track/album id when the
    /// enrichment match carries one, else a verified album search) after the Cover Art Archive.
    /// Needs Spotify app credentials; silently skipped without them.
    /// </summary>
    public bool EnableSpotifyCovers { get; set; } = true;

    /// <summary>Fall back to Deezer album search (<c>cover_xl</c>) when the Cover Art Archive has nothing.</summary>
    public bool EnableDeezerCovers { get; set; } = true;

    /// <summary>Fall back to iTunes album search (artwork upgraded to 3000x3000) as the last resort.</summary>
    public bool EnableAppleMusicCovers { get; set; } = true;

    // --- Artist images ---

    /// <summary>
    /// Resolve artist portrait images from Deezer (then Spotify) for the frontend artists views,
    /// cached in the database by normalized artist name.
    /// </summary>
    public bool EnableArtistImages { get; set; } = true;

    /// <summary>Days before an artist with no image on any provider is retried (catalogs grow). 0 = never.</summary>
    [Range(0, 365)]
    public int ArtistImageNotFoundRetryDays { get; set; } = 14;

    /// <summary>Days before a cached artist image URL is refreshed (CDN links can rot). 0 = never.</summary>
    [Range(0, 3650)]
    public int ArtistImageRefreshDays { get; set; } = 90;

    /// <summary>Max Cover Art Archive requests per second (MusicBrainz policy is 1 req/s per app).</summary>
    [Range(1, 5)]
    public int CoverArtArchiveRequestsPerSecond { get; set; } = 1;

    /// <summary>Minutes between periodic sweeps that fetch covers for built albums still missing one.</summary>
    [Range(1, 10080)]
    public int ExternalCoverArtSweepIntervalMinutes { get; set; } = 360;

    /// <summary>Max album folders attempted per external cover sweep cycle.</summary>
    [Range(1, 500)]
    public int ExternalCoverArtSweepBatchSize { get; set; } = 25;

    /// <summary>Days before an album with no cover on any provider is retried (catalogs grow). 0 = never.</summary>
    [Range(0, 365)]
    public int ExternalCoverArtNotFoundRetryDays { get; set; } = 7;

    /// <summary>Hours to back off before retrying an album whose cover fetch failed transiently.</summary>
    [Range(1, 720)]
    public int ExternalCoverArtFailedRetryHours { get; set; } = 24;

    /// <summary>Reject fetched images smaller than this (placeholder/error bodies, not real covers).</summary>
    [Range(0, 1_000_000)]
    public int ExternalCoverArtMinImageBytes { get; set; } = 4096;

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

    /// <summary>
    /// Skip tracker candidates the tracker itself says have never leaked ("Confirmed", "Rumored").
    /// Those entries document a song's existence, not a circulating file — so a file on disk can
    /// never legitimately be one, and leaving them in the pool is pure false-positive surface.
    /// </summary>
    public bool TrackerSkipUnobtainable { get; set; } = true;

    /// <summary>
    /// Confidence added when the file's <c>[Vn]</c> marker equals the candidate's version. Same-title
    /// versions tie on title score, so this (with the duration tiebreak) is what picks the right one.
    /// </summary>
    [Range(0.0, 1.0)]
    public double TrackerVersionMatchBonus { get; set; } = 0.05;

    /// <summary>Confidence multiplier when the file and candidate both carry a <c>[Vn]</c> marker and they disagree.</summary>
    [Range(0.0, 1.0)]
    public double TrackerVersionMismatchPenalty { get; set; } = 0.75;

    /// <summary>
    /// Minimum fuzzy ratio (0–100) for a tracker "OG filename" to stand in for a title match. Leaks
    /// usually circulate under that filename, so for an untagged file it's often the only real signal —
    /// but it's a weaker identity than a title, so it has to be near-exact.
    /// </summary>
    [Range(0.0, 100.0)]
    public double TrackerOgFilenameMinRatio { get; set; } = 90.0;

    // --- Kanye West "yetracker" community tracker (local JSON catalog, no API) ---

    /// <summary>
    /// Enable the yetracker (Kanye West) enrichment provider. Backed by a committed, offline-normalized
    /// JSON catalog (<c>Data/yetracker.json</c>) rather than a live API. On by default: it's gated to
    /// <see cref="YeTrackerArtistAllowlist"/> so it only fires for the artists it covers (Kanye West),
    /// and for those songs its catalog of leaks / alternate versions is richer than mainstream services.
    /// </summary>
    public bool EnableYeTrackerProvider { get; set; } = true;

    /// <summary>
    /// Artist names/aliases the yetracker covers. The provider only attempts a lookup when the song's
    /// resolved artist fuzzy-matches one of these — the catalog is single-artist (Kanye West).
    /// </summary>
    public string[] YeTrackerArtistAllowlist { get; set; } = ["Kanye West", "Ye", "Kanye", "Yeezy"];

    // --- Consensus / identity matching ---

    /// <summary>Minimum own-confidence for a provider candidate to act as a corroborating vote.</summary>
    [Range(0.0, 1.0)]
    public double ConsensusCorroborationFloor { get; set; } = 0.5;

    /// <summary>
    /// How long a song will keep waiting on a rate-limited provider before the consensus is
    /// finalized on the providers that did answer. A song two providers already agree on is matched
    /// immediately regardless; this only bounds songs that still need the throttled provider, so a
    /// persistently rate-limited provider (e.g. iTunes throttling a shared IP) can't stall
    /// enrichment indefinitely — past this window such songs match on the rest or surface for review.
    /// </summary>
    [Range(0, 1440)]
    public int RateLimitDeferralMinutes { get; set; } = 30;

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
    /// Additive confidence boost a name-based provider applies when a candidate's album agrees
    /// (fuzzily) with the file's album tag. Confirmation-only: a differing album is never penalized
    /// (a track legitimately appears on many releases), but the boost breaks ties in favour of the
    /// candidate on the file's own album so the original pressing is preferred over a compilation.
    /// </summary>
    [Range(0.0, 0.3)]
    public double AlbumAgreementConfidenceBoost { get; set; } = 0.05;

    /// <summary>
    /// When a corroborated recording is attributed to more than one release (e.g. the original album
    /// and a later "Greatest Hits" compilation), break ties toward the earliest, non-compilation
    /// (original) release for the album-level fields. Pure provider corroboration still wins first;
    /// this only decides equally-corroborated releases.
    /// </summary>
    public bool PreferOriginalRelease { get; set; } = true;

    /// <summary>
    /// Treat a lone <c>duration_mismatch</c> as advisory (not blocking) for wishlist / Spotify-Like
    /// download-origin files — those scanned from <see cref="DownloadDirectory"/> — when the enrichment
    /// cluster strongly corroborates the identity (AcoustID matched the file's own audio, or ≥2
    /// providers carry the same ISRC). These files are fetched from YouTube and stamped with a known
    /// Spotify identity, so their audio length routinely differs from the canonical master; without
    /// this they pile up in <see cref="EnrichmentStatus.NeedsReview"/> despite a correct, multi-provider
    /// match. Off for source-library files, where a duration gap remains a genuine wrong-recording
    /// signal. A change here heals the existing backlog only on an
    /// <see cref="Enrichment.EnrichmentAlgorithm.CurrentVersion"/> bump.
    /// </summary>
    public bool RelaxDownloadDurationMismatch { get; set; } = true;

    /// <summary>
    /// Minimum consensus confidence (with ≥2 agreeing providers) required to auto-overwrite a
    /// *good* existing curated value. Below this, a conflicting change is proposed for review
    /// rather than applied — so a curated library is never silently degraded.
    /// </summary>
    [Range(0.0, 1.0)]
    public double AutoUpgradeConfidence { get; set; } = 0.96;

    /// <summary>
    /// Build tracks that only reached <c>NeedsReview</c> (uncertain enrichment) instead of holding them
    /// out of the library until a human approves. Off by default: the build gate is normally strict
    /// <c>Matched</c>. When on, <c>NeedsReview</c> tracks are copied + tagged like any other, but flagged
    /// in the file — <c>GROUPING = "Needs Review"</c> (indexable/filterable in Navidrome, so you can make
    /// a self-emptying "Needs Review" smart playlist) plus a human-readable <c>COMMENT</c>. Approving or
    /// correcting a track later clears the flag on the next re-tag. Trade-off: possibly-wrong metadata
    /// (and therefore a possibly-wrong destination path) lands in the library until reviewed; correcting
    /// artist/album then moves the file, which resets a path-derived Navidrome track identity — harmless
    /// for never-played imports, but see the "needs review" flag before relying on play counts.
    /// </summary>
    public bool EnableBuildNeedsReview { get; set; } = false;

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
    /// How many times a track may fail its library build before it's quarantined out of the build
    /// queue. Without this, a track whose copy/tag throws is re-selected on every pass forever (the
    /// build query picks anything not yet <c>Done</c>), so a handful of un-writable files keep the
    /// builder in a permanent hot loop (issue #239). A quarantined track stays put until a manual
    /// re-build/re-enrich resets its counter.
    /// </summary>
    [Range(1, 100)]
    public int MaxLibraryBuildAttempts { get; set; } = 5;

    /// <summary>
    /// How long a freshly-matched track waits for its lyrics fetch to resolve before the builder tags it.
    /// Enrichment commits <c>Matched</c> *before* the LRCLIB lyrics fetch returns (see
    /// <c>EnrichmentOrchestrator</c>), so without this gate the build can copy+tag the destination file in
    /// that window and Navidrome reads a file with no embedded lyrics even though MusicHoarder's DB has
    /// them. The wait is bounded by <see cref="SongMetadata.EnrichedAtUtc"/>: once a match is older than
    /// this many minutes the track builds regardless (lyrics normally resolve in seconds; a manual approval
    /// never fetches lyrics, so an unbounded wait would stall it forever — the rebuild-on-lyrics-change
    /// interceptor still re-tags if lyrics arrive late). Forced re-tags and tracks with no title/artist
    /// (can't fetch lyrics) never wait. Set to 0 to disable the gate. Default 5.
    /// </summary>
    [Range(0, 1440)]
    public int LyricsBeforeBuildWaitMinutes { get; set; } = 5;

    /// <summary>
    /// The inline LRCLIB fetch only fires on the enrichment run that flips a song to <c>Matched</c>, so a
    /// song whose fetch was interrupted (shutdown/cancellation) — or that matched before a lyrics-matching
    /// fix shipped — is stranded at <see cref="LyricsStatus.NotFetched"/> with nothing to retry it. This
    /// startup-plus-periodic sweep re-attempts lyrics for those songs and re-queues a re-tag for any that
    /// were already built. Gated by <see cref="AutoStartPipeline"/> (it's auto-discovery, like the other
    /// sweeps). Set false to disable. Default on.
    /// </summary>
    public bool EnableLyricsBackfillSweep { get; set; } = true;

    /// <summary>
    /// Maximum number of stranded <see cref="LyricsStatus.NotFetched"/> songs the lyrics backfill sweep
    /// fetches per pass, so a large backlog is drained gradually across sweeps rather than hammering the
    /// public LRCLIB service in one burst. Default 200.
    /// </summary>
    [Range(1, 5000)]
    public int LyricsBackfillBatchSize { get; set; } = 200;

    /// <summary>
    /// Bounded concurrency for the lyrics backfill sweep's LRCLIB calls — kept low because LRCLIB is a free
    /// community service. Default 4.
    /// </summary>
    [Range(1, 32)]
    public int LyricsBackfillConcurrency { get; set; } = 4;

    /// <summary>
    /// LRCLIB is a community database that keeps growing, but a song's lyrics outcome is otherwise
    /// recorded once and never revisited: a track that 404'd stays <see cref="LyricsStatus.NotFound"/>
    /// forever, and one that only had unsynced lyrics stays <see cref="LyricsStatus.Fetched"/> even
    /// after someone contributes an LRC. This sweep re-queries those two cases (plus failed fetches) on
    /// an exponential backoff and re-tags any already-built file that gains lyrics. Gated by
    /// <see cref="AutoStartPipeline"/> like the other sweeps. Set false to disable. Default on.
    /// </summary>
    public bool EnableLyricsRecheckSweep { get; set; } = true;

    /// <summary>
    /// Delay before the *first* lyrics re-check; each further attempt doubles it, capped by
    /// <see cref="LyricsRecheckMaxCooldownDays"/> (see <c>SongMetadata.ComputeLyricsRecheckDelayDays</c>).
    /// Lyrics get contributed to LRCLIB over weeks, not minutes, so this is deliberately coarse.
    /// Default 7 days.
    /// </summary>
    [Range(1, 365)]
    public int LyricsRecheckCooldownDays { get; set; } = 7;

    /// <summary>
    /// Ceiling on the backoff above, so a song whose lyrics LRCLIB will probably never carry still gets
    /// an occasional look instead of drifting to a decade-long interval. Default 90 days.
    /// </summary>
    [Range(1, 3650)]
    public int LyricsRecheckMaxCooldownDays { get; set; } = 90;

    /// <summary>
    /// Maximum songs re-checked per sweep pass. Kept well below <see cref="LyricsBackfillBatchSize"/>:
    /// re-checks are speculative (most return the same "no lyrics" answer), so they should never crowd
    /// out first-time fetches or hammer LRCLIB. Default 50.
    /// </summary>
    [Range(1, 5000)]
    public int LyricsRecheckBatchSize { get; set; } = 50;

    /// <summary>
    /// Harmonize album-identity tags (release id, album name, year, disc count, compilation, release
    /// types, album-artist mbids) across all tracks that build into the same destination album folder,
    /// so a single on-disk album isn't split by a server's MusicBrainz-release grouping key (e.g.
    /// Navidrome's default <c>PID.Album</c>). Election is non-persisted (DB rows keep their per-track
    /// enrichment) and deterministic. Default on.
    /// </summary>
    public bool EnableAlbumIdentityReconciliation { get; set; } = true;

    /// <summary>
    /// Self-heal split albums: group all buildable songs by logical album (normalized artist +
    /// album title with an edition discriminator — year excluded, since a divergent year is the
    /// most common split), persist the reconciler-elected identity to member rows that disagree,
    /// and re-queue already-built ones for an in-place re-tag/relocate. This is the safeguard the
    /// folder-keyed reconciliation can't provide: siblings whose divergent album/year/artist put
    /// them in different destination folders, and Done rows tagged before the current election.
    /// Runs at the start of every build run, plus periodically while idle when the auto pipeline
    /// is on. Corrections are reversible (originals captured) and never bump EnrichedAtUtc.
    /// Requires <see cref="EnableAlbumIdentityReconciliation"/>. Default on.
    /// </summary>
    public bool EnableAlbumSplitSelfHeal { get; set; } = true;

    /// <summary>Minimum minutes between idle-time split-heal sweeps (auto pipeline only).</summary>
    [Range(5, 10080)]
    public int AlbumSplitHealIntervalMinutes { get; set; } = 360;

    /// <summary>
    /// Self-heal missing discrete artist credits: matched songs whose <c>Artists</c> list was never
    /// populated (they predate discrete-artist enrichment, or were matched by a provider without a
    /// per-artist credit) get it backfilled from the matched MusicBrainz/Spotify attempt already
    /// stored on the row — no provider calls — and already-built files are re-queued for an in-place
    /// re-tag. Without the discrete list the tag writer emits no per-artist ARTISTS frames and the
    /// combined display credit becomes one merged "artist" in Navidrome. Runs with the same cadence
    /// as the split-album heal (build-run start + idle sweeps). Reversible, never bumps
    /// EnrichedAtUtc. Default on.
    /// </summary>
    public bool EnableArtistCreditSelfHeal { get; set; } = true;

    /// <summary>
    /// When re-tagging an album (POST /api/enrichment/rebuild/album), first consolidate it against the
    /// persisted multi-provider canonical tracklist: rewrite each owned song's album title/year and
    /// track/disc number from the canonical track it matches (by recording-MBID + fuzzy title, never by
    /// the owned — possibly corrupt — position). This heals albums whose tracks were each enriched
    /// against a different release (so they split across year folders and carry duplicate track
    /// numbers). Falls back to a plain in-place re-tag when no canonical album exists. Default on.
    /// </summary>
    public bool EnableCanonicalDrivenBuild { get; set; } = true;

    /// <summary>
    /// Top-level folder name compilations (Various-Artists releases) are filed under, keyed by
    /// album rather than per-track artist so the album stays together. Empty falls back to
    /// "Various Artists" — the literal album-artist string every music server recognizes.
    /// </summary>
    public string CompilationFolderName { get; set; } = "Various Artists";

    // --- Playlist export (Spotify Liked Songs + playlists → on-disk M3U) ---

    /// <summary>
    /// Mirror the owner's Spotify Liked Songs and every playlist as static <c>.m3u8</c> files under
    /// <see cref="PlaylistsFolderName"/> in the destination library, so Navidrome/Plex/Jellyfin
    /// auto-import them. Each file lists the local built tracks matching the Spotify tracks, in
    /// Spotify order (Liked Songs by liked-date descending). Master switch for both the periodic
    /// background export and the manual regenerate trigger. Default on (no-op until Spotify connects).
    /// </summary>
    public bool EnablePlaylistExport { get; set; } = true;

    /// <summary>Minutes between background playlist-export runs. 0 disables the periodic export.</summary>
    [Range(0, 10080)]
    public int PlaylistExportIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Sub-folder of the destination directory the <c>.m3u8</c> files are written to. Track paths
    /// inside each file are relative to this folder (e.g. <c>../Artist/Album/01 - Title.flac</c>).
    /// </summary>
    public string PlaylistsFolderName { get; set; } = "Playlists";

    // --- Wishlist downloads (Spotify wishlist → downloader → source directory) ---

    /// <summary>
    /// Master switch for the wishlist downloader. When false the download worker idles and the API
    /// download trigger is a no-op (wishlist items are still tracked and synced, just never fetched).
    /// Enables the explicit <c>POST /api/wishlist/download</c> trigger; background auto-sweeping is
    /// additionally gated by <see cref="AutoDownloadWishlist"/>.
    /// </summary>
    public bool EnableWishlistDownloads { get; set; } = false;

    /// <summary>
    /// When true the download worker auto-sweeps Pending wishlist items in the background (no user action
    /// needed). When false, downloads only run on the explicit <c>POST /api/wishlist/download</c> trigger.
    /// Kept separate from <see cref="EnableWishlistDownloads"/> so an environment can expose the feature
    /// (manual, opt-in) without every instance auto-fetching on its own — e.g. PR previews stay manual,
    /// production auto-downloads for the owner. Requires <see cref="EnableWishlistDownloads"/>.
    /// </summary>
    public bool AutoDownloadWishlist { get; set; } = false;

    /// <summary>Name of the <c>IDownloadProvider</c> to use, resolved by <c>IDownloadProvider.Name</c>. Default "yt-dlp".
    /// Legacy single-provider form; ignored when <see cref="DownloadProviders"/> is non-empty.</summary>
    public string DownloadProvider { get; set; } = "yt-dlp";

    /// <summary>
    /// Ordered download-provider chain, e.g. <c>["slskd", "yt-dlp"]</c>. Each track tries providers
    /// in order, falling through to the next only when the current one reports "not found" (a real
    /// transient error stops the chain so a flaky provider doesn't silently burn the fallback's
    /// quota; the item goes Failed and the next sweep retries the whole chain). Empty falls back to
    /// the single <see cref="DownloadProvider"/>. Unconfigured providers report "not found", so a
    /// chain containing "slskd" is harmless on instances without slskd settings.
    /// </summary>
    public string[] DownloadProviders { get; set; } = [];

    /// <summary>Path to the yt-dlp binary. Must be on PATH or an absolute path.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Path to the ffmpeg binary yt-dlp uses for extraction/remux. Empty lets yt-dlp find it on PATH.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Number of concurrent downloads.</summary>
    [Range(1, 16)]
    public int DownloadConcurrency { get; set; } = 2;

    /// <summary>Number of Pending wishlist items claimed per download sweep batch.</summary>
    [Range(1, 500)]
    public int WishlistDownloadBatchSize { get; set; } = 20;

    /// <summary>Delay in seconds before the download worker re-checks for pending wishlist items.</summary>
    [Range(1, 300)]
    public int DownloadIdleDelaySeconds { get; set; } = 20;

    /// <summary>
    /// Absolute path to a writable staging directory that wishlist downloads are written into. Kept
    /// separate from <see cref="SourceDirectory"/> because the source library is usually a read-only
    /// mount. The scanner indexes this directory as an additional source root, so downloaded files flow
    /// through the normal scan → fingerprint → enrich → build pipeline. Required when
    /// <see cref="EnableWishlistDownloads"/> is on; the downloader idles if it's unset.
    /// </summary>
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>Target audio format/codec for the download (yt-dlp <c>--audio-format</c>). Default "opus" (YouTube native, no re-encode).</summary>
    public string DownloadAudioFormat { get; set; } = "opus";

    /// <summary>
    /// Minimum seconds yt-dlp waits before each download (<c>--sleep-interval</c>). A small built-in
    /// throttle so bulk wishlist runs don't hammer YouTube back-to-back, which is itself a strong
    /// bot-detection signal. 0 disables the wait.
    /// </summary>
    [Range(0, 60)]
    public int DownloadSleepSeconds { get; set; } = 2;

    /// <summary>
    /// Upper bound for the randomized pre-download wait (<c>--max-sleep-interval</c>); yt-dlp picks a
    /// random delay in [<see cref="DownloadSleepSeconds"/>, this]. Only applied when greater than the
    /// minimum — a randomized cadence looks less automated than a fixed one.
    /// </summary>
    [Range(0, 120)]
    public int DownloadMaxSleepSeconds { get; set; } = 6;

    /// <summary>
    /// Path to a Netscape-format cookies file passed to yt-dlp via <c>--cookies</c>. From a
    /// datacenter IP (preview/prod hosts) YouTube triggers a "Sign in to confirm you're not a bot"
    /// challenge; authenticated cookies from a logged-in account get past it. Empty → no cookies
    /// (works from residential IPs like local dev). The file is sensitive — mount it as a secret/volume,
    /// never commit it. See https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp
    /// <para>
    /// Tradeoff: once yt-dlp sees valid account cookies it drops every player client that can't carry
    /// them (android_vr, tv_simply, ios…) and is left with the web clients, which YouTube gates behind
    /// a GVS PO token — so a fetch can fail with "Requested format is not available" precisely
    /// <em>because</em> cookies were supplied. <see cref="Download.YtDlpDownloadProvider"/> retries such
    /// a fetch once anonymously; if that still fails, override the client set via
    /// <see cref="YtDlpExtraArgs"/> or supply a PO token there.
    /// </para>
    /// </summary>
    public string YtDlpCookiesPath { get; set; } = string.Empty;

    /// <summary>
    /// Extra command-line arguments appended verbatim to every yt-dlp invocation (space-separated,
    /// e.g. <c>--extractor-args youtube:player_client=tv --proxy http://...</c>). An escape hatch for
    /// anti-bot workarounds and proxies without a code change. Empty → none.
    /// </summary>
    public string YtDlpExtraArgs { get; set; } = string.Empty;

    // ── Music videos (companion YouTube clip, played muted behind the player) ──

    /// <summary>
    /// When true, wishlist/import downloads also fetch the track's music video from YouTube (a
    /// second yt-dlp invocation, so it works even when the audio came from slskd/spotiflac).
    /// Default OFF: clips are ~50–150 MB each and double YouTube traffic. The manual per-song
    /// "Fetch video" endpoint works regardless of this flag.
    /// </summary>
    public bool DownloadMusicVideos { get; set; } = false;

    /// <summary>Maximum video height requested from YouTube (yt-dlp format filter). 720 is the disk-space relief valve.</summary>
    [Range(144, 4320)]
    public int MusicVideoMaxHeight { get; set; } = 1080;

    /// <summary>
    /// Directory music videos are written to. Empty resolves to
    /// <c>&lt;DownloadDirectory&gt;/videos</c> — inside the staging root (one writable mount) but out
    /// of the scanner's way (video extensions aren't indexed regardless).
    /// </summary>
    public string MusicVideoDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Widest audio-vs-video offset (seconds, either direction) the aligner searches when the song's
    /// audio did not come from the video itself. Bounds the cross-correlation window and the extra
    /// video-fingerprint length.
    /// </summary>
    [Range(5, 300)]
    public int MusicVideoAlignMaxOffsetSeconds { get; set; } = 90;

    /// <summary>
    /// Maximum chromaprint bit-error-rate for an automatic alignment to be trusted. Above this the
    /// video stays <c>Unaligned</c> (offset 0, manual nudge in the UI). Identical recordings score
    /// ~0.05–0.15; unrelated audio ~0.5.
    /// </summary>
    [Range(0.05, 0.5)]
    public double MusicVideoAlignMaxBitErrorRate { get; set; } = 0.35;

    // ── Album completion (own ≥1 track → auto-fill the rest) ────────────────
    // A background sweep that compares each owned album against its reconciled CanonicalAlbum
    // tracklist and queues the missing tracks as WishlistItemOrigin.AlbumCompletion items. Songs that
    // arrive this way are stamped SongAcquisitionIntent.AlbumFill, which is what keeps the "My music"
    // view honest. Downloads reuse the wishlist pipeline entirely, so this shares the download
    // provider chain, the staging directory, the scanner and the whole enrichment budget — enabling it
    // materially increases external provider load, not just disk usage.

    /// <summary>
    /// Default for album completion, overlaid at runtime by
    /// <see cref="Persistence.RuntimeSettings.AlbumCompletionEnabled"/> so the owner can opt in (and
    /// back out) from the Settings UI without a redeploy — the same split downloads use. Default OFF:
    /// at a threshold of one owned track the candidate set is essentially every album in the library,
    /// so it must be an explicit choice. The hard gate is <see cref="EnableWishlistDownloads"/>:
    /// without a downloader, queueing tracks nothing will ever fetch is pointless.
    /// </summary>
    public bool EnableAlbumCompletion { get; set; } = false;

    /// <summary>
    /// Albums examined per sweep. With the default hourly interval this caps discovery at ~120
    /// albums/day, so first enabling the feature drains a large library over days instead of dumping
    /// the whole backlog into the downloader at once.
    /// </summary>
    [Range(1, 100)]
    public int AlbumCompletionAlbumsPerSweep { get; set; } = 5;

    /// <summary>Minutes between album-completion sweeps. Deliberately slow — this is a background drip, not a job.</summary>
    [Range(1, 1440)]
    public int AlbumCompletionSweepIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Most tracks queued for a single album in one pass. Backstop against a canonical "Complete Works"
    /// or 3-disc deluxe edition dumping hundreds of items because the owner happened to hold one track.
    /// </summary>
    [Range(1, 500)]
    public int AlbumCompletionMaxTracksPerAlbum { get; set; } = 30;

    /// <summary>
    /// The sweep short-circuits entirely while this many album-completion items are still Pending.
    /// This is the real backpressure: it bounds the queue against the downloader's actual throughput
    /// rather than against wall-clock, so a rate-limited provider slows discovery instead of letting an
    /// unbounded backlog build up. 0 disables the check.
    /// </summary>
    [Range(0, 100000)]
    public int AlbumCompletionMaxPendingItems { get; set; } = 200;

    /// <summary>Smallest canonical tracklist worth completing — stops the sweep "completing" singles.</summary>
    [Range(2, 100)]
    public int AlbumCompletionMinCanonicalTracks { get; set; } = 2;

    /// <summary>
    /// Days before a filled album is examined again (a later edition may add tracks). A canonical album
    /// that is re-fetched forces a re-sweep regardless of this. Skipped albums are never revisited on a
    /// timer — a compilation does not stop being one.
    /// </summary>
    [Range(0, 3650)]
    public int AlbumCompletionRevisitDays { get; set; } = 30;

    /// <summary>
    /// Skip canonical tracks flagged <see cref="Persistence.CanonicalAlbumTrack.IsContested"/> — not
    /// every provider in the winning cluster backs them, so they are typically deluxe/bonus phantoms no
    /// downloader will find. Default on: they generate pure NotFound noise.
    /// </summary>
    public bool AlbumCompletionSkipContestedTracks { get; set; } = true;

    /// <summary>
    /// MusicBrainz release-group types (matched case-insensitively against the <c>;</c>-joined
    /// <see cref="Persistence.SongMetadata.ReleaseTypes"/>) that disqualify an album from completion.
    /// Compilations are excluded because owning one track from a sampler says nothing about wanting the
    /// other thirty-nine — and because a canonical track carries no per-track artist, so every track
    /// would be searched under the album artist ("Various Artists - Some Song") and fetch garbage.
    /// </summary>
    public string[] AlbumCompletionSkipReleaseTypes { get; set; } = ["compilation"];

    // ── Automatic quality upgrades ──────────────────────────────────────────
    // A background sweep that re-acquires lossy library tracks as lossless via the configured
    // DownloadProviders chain (spotiflac/slskd), reusing the manual-upgrade request→merge pipeline.
    // Not to be confused with AutoDownloadWishlist (fetching new tracks) or AutoUpgradeConfidence
    // (an enrichment-match threshold).

    /// <summary>
    /// Master switch for the automatic quality-upgrade sweep. Default ON, but inert unless a
    /// lossless-capable download provider (slskd or spotiflac) is configured — with none, the sweep
    /// no-ops, so this turns itself on the moment such a provider is wired up.
    /// </summary>
    public bool EnableAutomaticQualityUpgrades { get; set; } = true;

    /// <summary>Max lossy tracks queued for upgrade per sweep pass — bounds external-service load.</summary>
    [Range(1, 500)]
    public int QualityUpgradeBatchSize { get; set; } = 10;

    /// <summary>Minutes between automatic upgrade sweeps.</summary>
    [Range(1, 1440)]
    public int QualityUpgradeSweepIntervalMinutes { get; set; } = 60;

    /// <summary>Days to wait before re-attempting a song whose last upgrade attempt found nothing (or
    /// failed), so a track with no better source isn't re-searched every sweep.</summary>
    [Range(0, 3650)]
    public int QualityUpgradeCooldownDays { get; set; } = 30;

    /// <summary>
    /// Minimum acoustic-fingerprint similarity (0..1) the merge requires between a downloaded upgrade
    /// and the track it would replace, guarding against a source that shares only duration. 0 disables
    /// the check (duration-only). Fingerprints that can't be decoded skip the gate (fail-open).
    /// </summary>
    [Range(0, 1)]
    public double QualityUpgradeFingerprintMinSimilarity { get; set; } = 0.75;

    // --- Duplicate detection ---

    /// <summary>
    /// Minimum acoustic-fingerprint similarity (0..1) for a candidate pair to be auto-confirmed as
    /// duplicates (and flagged via <c>IsDuplicate</c>). Deliberately stricter than
    /// <see cref="QualityUpgradeFingerprintMinSimilarity"/>: an upgrade merge is user-initiated per
    /// track, while a confirmed duplicate silently drops a file from the build.
    /// </summary>
    [Range(0, 1)]
    public double DuplicateFingerprintMinSimilarity { get; set; } = 0.85;

    /// <summary>
    /// Similarity (0..1) below which a decodable candidate pair is dropped entirely — two
    /// fingerprints that strongly disagree are affirmative evidence of different recordings, so the
    /// pair isn't even surfaced as suspected. Pairs scoring between this and
    /// <see cref="DuplicateFingerprintMinSimilarity"/> surface as suspected for manual review.
    /// </summary>
    [Range(0, 1)]
    public double DuplicateFingerprintRejectSimilarity { get; set; } = 0.55;

    /// <summary>Max duration delta (seconds) for two same-metadata songs to pair as duplicate
    /// candidates. Identifier-based candidates (shared AcoustID/ISRC) get twice this slack.</summary>
    [Range(0, 60)]
    public int DuplicateDurationToleranceSeconds { get; set; } = 3;

    /// <summary>
    /// Largest metadata/identifier block that will be pairwise-expanded during candidate generation.
    /// A pathological block (e.g. hundreds of untitled tracks sharing one normalized key) is skipped
    /// with a warning instead of generating O(n²) comparisons.
    /// </summary>
    [Range(2, 1000)]
    public int DuplicateMaxBlockSize { get; set; } = 50;

    /// <summary>Fuzzy ratio (0–100) above which two distinct artist spellings are suggested as the
    /// same artist in the artist-duplicates view.</summary>
    [Range(0, 100)]
    public double ArtistMergeFuzzyThreshold { get; set; } = 92;

    /// <summary>Fuzzy ratio (0–100) above which two album titles under one artist are suggested as
    /// the same album in the album-duplicates view.</summary>
    [Range(0, 100)]
    public double AlbumMergeFuzzyThreshold { get; set; } = 90;
}