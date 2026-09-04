using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

/// <summary>
/// An unordered pair of song ids stored ordered (<see cref="Low"/> &lt; <see cref="High"/>), so one
/// pair has exactly one key however it was discovered — the same convention
/// <see cref="SongDuplicateLink"/> persists as <c>SongIdLow</c>/<c>SongIdHigh</c>.
/// </summary>
public readonly record struct SongIdPair(int Low, int High)
{
    /// <summary>Builds the pair for two ids in either order.</summary>
    public static SongIdPair Of(int a, int b) => a < b ? new SongIdPair(a, b) : new SongIdPair(b, a);

    /// <summary>The key a persisted link row corresponds to.</summary>
    public static SongIdPair Of(SongDuplicateLink link) => new(link.SongIdLow, link.SongIdHigh);
}
