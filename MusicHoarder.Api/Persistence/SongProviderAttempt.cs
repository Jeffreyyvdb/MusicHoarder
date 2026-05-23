using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public enum EnrichmentProvider
{
    AcoustID = 0,
    SpotifyAPI = 1,
    MusicBrainzWeb = 2,
    Tracker = 3,
    Deezer = 4,
    AppleMusic = 5,
    YeTracker = 6,
}

public enum ProviderAttemptStatus
{
    Pending = 0,
    Matched = 1,
    NoMatch = 2,
    RateLimited = 3,
    Failed = 4,
}

public class SongProviderAttempt
{
    [Key]
    public int Id { get; set; }

    public int SongId { get; set; }
    public SongMetadata Song { get; set; } = null!;

    public EnrichmentProvider Provider { get; set; }
    public ProviderAttemptStatus Status { get; set; }
    public DateTime AttemptedAtUtc { get; set; }

    /// <summary>When a <see cref="ProviderAttemptStatus.RateLimited"/> attempt may be retried.</summary>
    public DateTime? RetryAfterUtc { get; set; }

    /// <summary>
    /// For terminal <see cref="ProviderAttemptStatus.NoMatch"/> / <see cref="ProviderAttemptStatus.Failed"/>
    /// attempts: when the provider should be retried (catalogs grow over time). Null = never.
    /// </summary>
    public DateTime? NextRetryAfterUtc { get; set; }

    public string? MatchedDataJson { get; set; }
    public string? Error { get; set; }
}
