using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public enum EnrichmentProvider
{
    AcoustID = 0,
    SpotifyAPI = 1,
    MusicBrainzWeb = 2,
    Tracker = 3,
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
    public DateTime? RetryAfterUtc { get; set; }
    public string? MatchedDataJson { get; set; }
    public string? Error { get; set; }
}
