namespace MusicHoarder.Api.Navidrome;

/// <summary>
/// A song as returned by Navidrome's Subsonic API (<c>getStarred2</c> / <c>search3</c>). <see cref="Path"/>
/// is relative to its library root — the join key back to a MusicHoarder song's destination/source
/// relative path. <see cref="MusicBrainzId"/> is the recording id and is often empty for un-enriched
/// (source-library) files, which is why path is the primary match key.
/// </summary>
public sealed record NavidromeSong(
    string Id,
    string? Title,
    string? Artist,
    string? Album,
    string? Path,
    string? MusicBrainzId,
    int? DurationSeconds,
    string? Suffix);

/// <summary>Thrown when Navidrome returns a Subsonic <c>failed</c> envelope (auth error, bad id, …).</summary>
public sealed class NavidromeApiException(int code, string message)
    : Exception($"Navidrome Subsonic error {code}: {message}")
{
    public int Code { get; } = code;
}
