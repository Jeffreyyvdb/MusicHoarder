namespace MusicHoarder.Api.Library;

/// <summary>
/// The audio file cannot be opened by the tagger at all — a corrupt, truncated or otherwise
/// malformed container, not a tag-level problem. Distinct from every other build failure because a
/// retry cannot fix it: the builder quarantines the row on the FIRST occurrence instead of copying
/// the same broken bytes <c>MaxLibraryBuildAttempts</c> times.
/// <para>
/// The case this was written for: an Ogg Opus download whose logical stream carries no
/// <c>OpusTags</c> comment header. TagLib 2.3.0 then constructs its Xiph comment from a null packet
/// and the build fails with "Value cannot be null. (Parameter 'data')" — a message that says nothing
/// about the file. ffmpeg rejects the very same file, so it is genuinely broken (an incomplete or
/// mangled download), not a TagLib quirk, and it is also unplayable and un-fingerprintable.
/// </para>
/// </summary>
public sealed class UnreadableAudioFileException(string path, Exception inner)
    : Exception(
        $"Not a readable audio file — corrupt, truncated or an incomplete download "
        + $"({inner.GetType().Name}: {inner.Message})",
        inner)
{
    /// <summary>The file that could not be opened (the build's temp copy of the source).</summary>
    public string Path { get; } = path;
}
