using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

public class MusicEnricherOptions
{
    public const string SectionName = "MusicEnricher";

    [Required(ErrorMessage = "MusicEnricher:SourceDirectory is required.")]
    public string SourceDirectory { get; set; } = string.Empty;

    [Required(ErrorMessage = "MusicEnricher:DestinationDirectory is required.")]
    public string DestinationDirectory { get; set; } = string.Empty;

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

    /// <summary>Enable the AcoustID enrichment provider (fingerprint → MusicBrainz via AcoustID).</summary>
    public bool EnableAcoustIdProvider { get; set; } = true;

    /// <summary>Enable the MusicBrainz web-service enrichment provider (ISRC / artist+title search).</summary>
    public bool EnableMusicBrainzWebProvider { get; set; } = false;

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

    /// <summary>Number of tracks processed per library-build cycle.</summary>
    [Range(1, 10000)]
    public int LibraryBuilderBatchSize { get; set; } = 100;

    /// <summary>Number of concurrent copy/tag workers per library-build cycle.</summary>
    [Range(1, 64)]
    public int LibraryBuilderWorkerConcurrency { get; set; } = 2;

    /// <summary>Delay in seconds before retrying when no tracks are pending library build.</summary>
    [Range(1, 300)]
    public int LibraryBuilderIdleDelaySeconds { get; set; } = 20;
}