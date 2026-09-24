using MusicHoarder.Api.Audio;

namespace MusicHoarder.Api.Tests.Audio;

/// <summary>
/// Stands in for ffmpeg in the stream endpoint tests. Records every source it was asked to convert
/// and answers with <c>rendition(source)</c>; without one it fails the way ffmpeg does, with a
/// message that names the path, so tests can check the path never reaches a response.
/// </summary>
internal sealed class FakeStreamTranscoder(Func<string, string>? rendition = null) : IStreamTranscoder
{
    public List<string> Requested { get; } = [];

    public Task<string> GetAacRenditionAsync(string sourcePath, CancellationToken ct)
    {
        Requested.Add(sourcePath);
        return rendition is null
            ? Task.FromException<string>(new InvalidOperationException($"ffmpeg exited with code 1: {sourcePath}: Invalid data"))
            : Task.FromResult(rendition(sourcePath));
    }
}
