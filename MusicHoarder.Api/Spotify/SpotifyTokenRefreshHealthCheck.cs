using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Degraded once the Spotify token-refresh loop has failed several times in a row — a stale refresh
/// token silently breaks every Spotify-dependent stage. Degraded (not Unhealthy) and not tagged "live",
/// so an offline/unreachable Spotify never fails the container's liveness probe.
/// </summary>
public class SpotifyTokenRefreshHealthCheck(SpotifyTokenRefreshHealth health) : IHealthCheck
{
    /// <summary>Consecutive failures before the check reports Degraded.</summary>
    public const int DegradedThreshold = 3;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var s = health.Snapshot();
        var data = new Dictionary<string, object>
        {
            ["consecutiveFailures"] = s.ConsecutiveFailures,
            ["lastSuccessUtc"] = s.LastSuccessUtc?.ToString("o") ?? "never",
        };
        if (s.LastError is not null) data["lastError"] = s.LastError;

        if (s.ConsecutiveFailures >= DegradedThreshold)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Spotify token refresh has failed {s.ConsecutiveFailures} times in a row.", data: data));

        return Task.FromResult(HealthCheckResult.Healthy("Spotify token refresh healthy.", data));
    }
}
