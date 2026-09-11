namespace MusicHoarder.Api.Storage;

/// <summary>
/// Holds the latest <see cref="StorageUsageSnapshot"/> and the single-flight flag around producing
/// the next one. Measuring walks every managed root, so it never runs on a request thread: endpoints
/// read <see cref="Get"/> and, when they want fresher numbers, <see cref="RequestRefresh"/> — which
/// only wakes <see cref="StorageUsageBackgroundService"/>, the one owner of scopes, cancellation and
/// error logging.
/// </summary>
public sealed class StorageUsageSnapshotStore
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _requests = new(0, 1);
    private StorageUsageSnapshot? _latest;
    private bool _computing;
    /// <summary>
    /// A refresh was asked for and no measurement has begun for it yet. Tracked as its own flag,
    /// not read off the semaphore: the background service consumes the semaphore a moment before it
    /// calls <see cref="TryBegin"/>, and a client reading the store in that gap must still hear that
    /// a measurement is coming — otherwise it stops polling and shows the old snapshot for good.
    /// </summary>
    private bool _pending;
    private string? _lastError;

    public StorageUsageResponse Get()
    {
        lock (_lock)
            return new StorageUsageResponse(_computing || _pending, _latest, _lastError);
    }

    public bool IsComputing
    {
        get { lock (_lock) return _computing; }
    }

    /// <summary>
    /// Asks for a fresh measurement. False when one is already running (its result is imminent);
    /// true when the request is queued — or was already queued, which is the same promise.
    /// </summary>
    public bool RequestRefresh()
    {
        lock (_lock)
        {
            if (_computing) return false;
            if (_pending) return true; // one pending request is all a refresh needs
            _pending = true;
            try
            {
                _requests.Release();
            }
            catch (SemaphoreFullException)
            {
                // The timer path consumed a stale signal's flag but left its count; still queued.
            }
            return true;
        }
    }

    /// <summary>Completes when a refresh has been requested. Consumes the request.</summary>
    public Task WaitForRequestAsync(CancellationToken ct) => _requests.WaitAsync(ct);

    public bool TryBegin()
    {
        lock (_lock)
        {
            if (_computing) return false;
            _pending = false;
            _computing = true;
            return true;
        }
    }

    public void Complete(StorageUsageSnapshot snapshot)
    {
        lock (_lock)
        {
            _latest = snapshot;
            _lastError = null;
            _computing = false;
        }
    }

    public void Fail(string error)
    {
        lock (_lock)
        {
            _lastError = error;
            _computing = false;
        }
    }
}
