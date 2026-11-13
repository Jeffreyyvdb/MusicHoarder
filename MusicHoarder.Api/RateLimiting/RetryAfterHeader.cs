namespace MusicHoarder.Api.RateLimiting;

/// <summary>Reads a response's <c>Retry-After</c> in either form the HTTP spec allows.</summary>
public static class RetryAfterHeader
{
    /// <summary>
    /// The longest <c>Retry-After</c> a catalog client sleeps out in place before retrying. A longer
    /// one is thrown as a <c>ProviderRateLimitedException</c>, so the song is deferred and the
    /// enrichment worker (and the provider's single orchestrator slot) is freed instead of held for
    /// as long as the server asks — hours, for an exhausted quota (issue #550). Spotify's catalog
    /// client takes the same default from <c>MusicEnricher:SpotifyApiMaxInlineRetryAfterSeconds</c>.
    /// </summary>
    public static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The wait the server asked for: the delta-seconds form as is, the HTTP-date form as the time
    /// left until <paramref name="now"/> reaches it. <c>null</c> when there is no header, or the date
    /// has already passed.
    /// </summary>
    public static TimeSpan? Read(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date && date > now)
            return date - now;
        return null;
    }
}
