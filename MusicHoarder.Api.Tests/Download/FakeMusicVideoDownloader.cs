using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Scriptable <see cref="IMusicVideoDownloader"/> for tests. Defaults to "no video found"; assign
/// <see cref="OnDownload"/> to script results. Records every call.
/// </summary>
public sealed class FakeMusicVideoDownloader : IMusicVideoDownloader
{
    public List<MusicVideoFetchRequest> Calls { get; } = [];

    public Func<MusicVideoFetchRequest, MusicVideoDownloadResult>? OnDownload { get; set; }

    public Task<MusicVideoDownloadResult> DownloadAsync(MusicVideoFetchRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        return Task.FromResult(OnDownload?.Invoke(request) ?? MusicVideoDownloadResult.Missing());
    }

    public List<MusicVideoFetchRequest> SuggestCalls { get; } = [];

    public Func<MusicVideoFetchRequest, IReadOnlyList<MusicVideoCandidate>>? OnSuggest { get; set; }

    public Task<IReadOnlyList<MusicVideoCandidate>> SuggestAsync(
        MusicVideoFetchRequest request, int probeLimit, CancellationToken ct)
    {
        SuggestCalls.Add(request);
        return Task.FromResult(OnSuggest?.Invoke(request) ?? (IReadOnlyList<MusicVideoCandidate>)[]);
    }

    public string ResolveVideoDirectory() => "/downloads/videos";
}
