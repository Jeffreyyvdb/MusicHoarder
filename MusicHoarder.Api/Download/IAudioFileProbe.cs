namespace MusicHoarder.Api.Download;

/// <summary>What a cheap header read of an audio file can tell us. Null duration = not readable.</summary>
public readonly record struct AudioProbeResult(int DurationMs);

/// <summary>
/// Reads an audio file's header far enough to prove it is a parseable track with a known duration.
/// Abstracted so the staged-source release can be exercised over a mock filesystem in tests; the
/// production implementation is TagLib, the same reader the scanner already trusts.
/// </summary>
public interface IAudioFileProbe
{
    /// <summary>Returns null when the file cannot be opened or parsed as audio.</summary>
    AudioProbeResult? Probe(string path);
}

public sealed class TagLibAudioFileProbe(ILogger<TagLibAudioFileProbe> logger) : IAudioFileProbe
{
    public AudioProbeResult? Probe(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var duration = file.Properties?.Duration.TotalMilliseconds ?? 0;
            return duration > 0 ? new AudioProbeResult((int)duration) : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Audio probe could not read {Path}", path);
            return null;
        }
    }
}
