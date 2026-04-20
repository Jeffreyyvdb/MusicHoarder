namespace MusicHoarder.Api.Enrichment;

public class ProviderRateLimitedException(TimeSpan retryAfter)
    : Exception($"Rate limited. Retry after {retryAfter.TotalSeconds:F0}s.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
