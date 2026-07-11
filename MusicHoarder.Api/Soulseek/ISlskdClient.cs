namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Thin typed client for the slskd v0 REST API. All calls no-op (null/empty/false) when the
/// integration is unconfigured, so callers can probe without guarding.
/// </summary>
public interface ISlskdClient
{
    /// <summary>Starts a network search and returns its state (with the id used to poll/collect).</summary>
    Task<SlskdSearchState?> StartSearchAsync(string searchText, CancellationToken ct);

    Task<SlskdSearchState?> GetSearchAsync(Guid searchId, CancellationToken ct);

    Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(Guid searchId, CancellationToken ct);

    /// <summary>Enqueues a download of one remote file from a peer. True when slskd accepted the request.</summary>
    Task<bool> EnqueueDownloadAsync(string username, string filename, long size, CancellationToken ct);

    /// <summary>All downloads currently known for a peer, flattened across directories.</summary>
    Task<IReadOnlyList<SlskdTransfer>> GetDownloadsAsync(string username, CancellationToken ct);

    Task CancelDownloadAsync(string username, string transferId, bool remove, CancellationToken ct);

    /// <summary>Soulseek server connection status, for the settings/status UI. Null when unreachable.</summary>
    Task<SlskdApplicationState?> GetApplicationStateAsync(CancellationToken ct);
}
