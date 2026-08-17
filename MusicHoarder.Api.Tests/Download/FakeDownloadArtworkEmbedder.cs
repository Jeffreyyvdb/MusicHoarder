using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Recording <see cref="IDownloadArtworkEmbedder"/> for tests — the real one downloads an image and
/// rewrites the file's tags, neither of which the processor tests want. Reports "no art written" and
/// keeps every (file, url) pair it was handed.
/// </summary>
public sealed class FakeDownloadArtworkEmbedder : IDownloadArtworkEmbedder
{
    public List<(string FilePath, string? ImageUrl)> Calls { get; } = [];

    public Task<bool> EmbedAsync(string filePath, string? imageUrl, CancellationToken ct = default)
    {
        Calls.Add((filePath, imageUrl));
        return Task.FromResult(false);
    }
}
