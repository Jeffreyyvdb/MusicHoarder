using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

public class MusicEnricherOptions
{
    public const string SectionName = "MusicEnricher";

    [Required(ErrorMessage = "MusicEnricher:SourceDirectory is required.")]
    public string SourceDirectory { get; set; } = string.Empty;

    [Required(ErrorMessage = "MusicEnricher:DestinationDirectory is required.")]
    public string DestinationDirectory { get; set; } = string.Empty;

    [Required(ErrorMessage = "MusicEnricher:TempDirectory is required.")]
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>Maximum concurrent file reads (tag reading + fpcalc) for SMB safety.</summary>
    [Range(1, 64)]
    public int SmbConcurrency { get; set; } = 8;

    /// <summary>Number of records accumulated before a DB write is flushed.</summary>
    [Range(1, 10000)]
    public int DbBatchSize { get; set; } = 100;

    /// <summary>Path to the fpcalc binary (Chromaprint). Must be on PATH or an absolute path.</summary>
    public string FpcalcPath { get; set; } = "fpcalc";

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
}
