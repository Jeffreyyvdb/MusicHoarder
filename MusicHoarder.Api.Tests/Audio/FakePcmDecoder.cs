using MusicHoarder.Api.Audio;

namespace MusicHoarder.Api.Tests.Audio;

/// <summary>
/// Stands in for ffmpeg. The file "decodes" to <paramref name="producedFrames"/> frames (by default the
/// length it reports) of a byte pattern that encodes each byte's own offset in the data, so a test can
/// tell exactly which bytes a range was served from.
/// </summary>
internal sealed class FakePcmDecoder(long? frames = 1_000, long? producedFrames = null, bool busy = false) : IPcmDecoder
{
    public List<(string Path, long StartFrame)> Opened { get; } = [];

    public Task<long?> CountFramesAsync(string path, CancellationToken ct) => Task.FromResult(frames);

    public Stream? Open(string path, long startFrame)
    {
        if (busy)
            return null;
        Opened.Add((path, startFrame));
        var total = (producedFrames ?? frames ?? 0) * WavStreamResult.BlockAlign;
        var start = startFrame * WavStreamResult.BlockAlign;
        var bytes = new byte[Math.Max(0, total - start)];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = PatternAt(start + i);
        return new MemoryStream(bytes);
    }

    /// <summary>The byte the fake decoder produces at <paramref name="dataOffset"/> into the PCM.</summary>
    public static byte PatternAt(long dataOffset) => (byte)(dataOffset % 251);
}
