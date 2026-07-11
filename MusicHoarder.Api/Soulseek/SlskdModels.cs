namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Wire DTOs for the slskd v0 API. Parsed with case-insensitive web defaults; everything optional
/// is nullable so a shape drift in slskd degrades to "no data" instead of a crash.
/// </summary>
public sealed record SlskdSearchState(
    Guid Id,
    string? State,
    string? SearchText,
    int ResponseCount,
    int FileCount)
{
    /// <summary>slskd reports composite states like <c>"Completed, TimedOut"</c> — any Completed counts.</summary>
    public bool IsComplete => State?.Contains("Completed", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record SlskdSearchResponse(
    string Username,
    bool HasFreeUploadSlot,
    int QueueLength,
    long UploadSpeed,
    IReadOnlyList<SlskdFile>? Files);

/// <summary>
/// One remote file in a search response. <paramref name="Filename"/> is the peer's full remote path
/// (usually Windows-style backslashes). <paramref name="Length"/> is duration in seconds;
/// <paramref name="BitRate"/> is the advertised (untrusted) bitrate in kbps.
/// </summary>
public sealed record SlskdFile(
    string Filename,
    long Size,
    int? BitRate,
    int? Length,
    string? Extension,
    bool IsLocked = false)
{
    /// <summary>Lowercased extension with leading dot, from the metadata field or the remote path.</summary>
    public string NormalizedExtension
    {
        get
        {
            var ext = !string.IsNullOrWhiteSpace(Extension)
                ? (Extension.StartsWith('.') ? Extension : "." + Extension)
                : Path.GetExtension(RemoteLeafName);
            return ext.ToLowerInvariant();
        }
    }

    /// <summary>The bare file name at the end of the remote path (either slash style).</summary>
    public string RemoteLeafName
    {
        get
        {
            var idx = Filename.LastIndexOfAny(['\\', '/']);
            return idx >= 0 ? Filename[(idx + 1)..] : Filename;
        }
    }
}

/// <summary>A transfer as reported by <c>GET /api/v0/transfers/downloads</c> (flattened).</summary>
public sealed record SlskdTransfer(
    string Id,
    string Username,
    string Filename,
    long Size,
    string? State,
    long BytesTransferred)
{
    public bool IsSucceeded => HasState("Succeeded");
    public bool IsErrored => HasState("Errored") || HasState("Rejected") || HasState("Cancelled") || HasState("TimedOut");
    public bool IsComplete => HasState("Completed");

    private bool HasState(string token) =>
        State?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Server-connection slice of <c>GET /api/v0/application</c>, for the settings/status UI.</summary>
public sealed record SlskdApplicationState(string? ServerState, string? Version)
{
    public bool IsConnected =>
        ServerState?.Contains("Connected", StringComparison.OrdinalIgnoreCase) == true
        && ServerState.Contains("LoggedIn", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A downloadable candidate elected from search responses (one file + its peer's stats).</summary>
public sealed record SlskdCandidate(
    string Username,
    SlskdFile File,
    bool HasFreeUploadSlot,
    int QueueLength,
    long UploadSpeed);
