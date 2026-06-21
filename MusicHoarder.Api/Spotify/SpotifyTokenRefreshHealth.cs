namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Shared, thread-safe health state for the Spotify token-refresh loop. The background service updates
/// it; <see cref="SpotifyTokenRefreshHealthCheck"/> reads it. A persistently failing refresh silently
/// breaks every Spotify-dependent stage (library match, wishlist sync, the Spotify enrichment provider),
/// so tracking consecutive failures gives an early signal before those degrade. Registered as a singleton.
/// </summary>
public class SpotifyTokenRefreshHealth
{
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private string? _lastError;
    private DateTime? _lastSuccessUtc;
    private DateTime? _lastFailureUtc;

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _lastError = null;
            _lastSuccessUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Records a failure and returns the new consecutive-failure count.</summary>
    public int RecordFailure(string error)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastError = error;
            _lastFailureUtc = DateTime.UtcNow;
            return _consecutiveFailures;
        }
    }

    public SpotifyTokenRefreshSnapshot Snapshot()
    {
        lock (_lock)
            return new SpotifyTokenRefreshSnapshot(_consecutiveFailures, _lastError, _lastSuccessUtc, _lastFailureUtc);
    }
}

public record SpotifyTokenRefreshSnapshot(
    int ConsecutiveFailures,
    string? LastError,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc);
