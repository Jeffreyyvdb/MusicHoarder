namespace MusicHoarder.Api.Spotify;

public class SpotifyTokenRefreshService(
    ISpotifyOAuthService spotifyOAuth,
    ILogger<SpotifyTokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Spotify token refresh service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await spotifyOAuth.EnsureValidTokenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during Spotify token refresh check");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Spotify token refresh service stopped");
    }
}
