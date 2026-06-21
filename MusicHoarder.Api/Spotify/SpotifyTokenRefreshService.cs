using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Spotify;

public class SpotifyTokenRefreshService(
    ISpotifyOAuthService spotifyOAuth,
    IOptions<SpotifyOptions> spotifyOptions,
    SpotifyTokenRefreshHealth health,
    ILogger<SpotifyTokenRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(
            Math.Clamp(spotifyOptions.Value.SpotifyTokenRefreshIntervalSeconds, 15, 180));

        logger.LogInformation("Spotify token refresh service started (interval {Seconds}s)", checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await spotifyOAuth.EnsureValidTokenAsync(stoppingToken);
                health.RecordSuccess();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var failures = health.RecordFailure(ex.Message);

                // Escalate to Error exactly once, when the streak first crosses the degraded threshold,
                // so a persistent failure surfaces loudly without spamming the log every interval after.
                if (failures == SpotifyTokenRefreshHealthCheck.DegradedThreshold)
                    logger.LogError(ex, "Spotify token refresh has failed {Failures} times in a row; downstream Spotify stages will degrade until it recovers", failures);
                else
                    logger.LogWarning(ex, "Error during Spotify token refresh check (failure #{Failures})", failures);
            }

            try
            {
                await Task.Delay(checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Spotify token refresh service stopped");
    }
}
