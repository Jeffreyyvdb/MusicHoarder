using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// One song's resolved cover, kept in memory so a warm <c>/songs/{id}/cover</c> request doesn't
/// re-run the whole resolution chain (directory enumeration + TagLib embedded-art extraction +
/// file-exists probes) per image. The identity file is re-statted at most once per
/// <see cref="ICoverResolutionCache.StatInterval"/> per entry; a changed stamp invalidates the
/// entry, and cover-writing pipeline stages invalidate by path immediately.
/// </summary>
public sealed record CoverResolutionEntry(
    string AudioFilePath,
    string? CoverFilePath,
    string? CoverContentType,
    string IdentityPath,
    long IdentityStamp,
    string? VideoThumbnailPath,
    string Version,
    DateTime ResolvedAtUtc,
    DateTime LastStatAtUtc);

public interface ICoverResolutionCache
{
    /// <summary>How often an entry's identity file is re-statted for changes.</summary>
    TimeSpan StatInterval { get; }

    bool TryGet(int songId, out CoverResolutionEntry? entry);

    void Store(int songId, CoverResolutionEntry entry);

    void Invalidate(int songId);

    /// <summary>Invalidates every entry whose cover/identity/audio file lives under <paramref name="directory"/> (a cover was written there).</summary>
    void InvalidatePath(string directory);

    /// <summary>True when the entry's identity file should be re-statted now.</summary>
    bool RestatDue(CoverResolutionEntry entry, DateTime nowUtc);

    /// <summary>Records that the identity file was just re-statted (and unchanged).</summary>
    void NoteStat(int songId, DateTime nowUtc);
}

public sealed class CoverResolutionCache : ICoverResolutionCache
{
    public TimeSpan StatInterval { get; } = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<int, CoverResolutionEntry> _bySong = new();

    public bool TryGet(int songId, out CoverResolutionEntry? entry) =>
        _bySong.TryGetValue(songId, out entry);

    public void Store(int songId, CoverResolutionEntry entry) => _bySong[songId] = entry;

    public void Invalidate(int songId) => _bySong.TryRemove(songId, out _);

    public void InvalidatePath(string directory)
    {
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var pair in _bySong)
        {
            var e = pair.Value;
            if (e.IdentityPath.StartsWith(prefix, StringComparison.Ordinal)
                || (e.CoverFilePath is not null && e.CoverFilePath.StartsWith(prefix, StringComparison.Ordinal))
                || e.AudioFilePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                _bySong.TryRemove(pair.Key, out _);
            }
        }
    }

    public bool RestatDue(CoverResolutionEntry entry, DateTime nowUtc) =>
        nowUtc - entry.LastStatAtUtc >= StatInterval;

    public void NoteStat(int songId, DateTime nowUtc)
    {
        if (_bySong.TryGetValue(songId, out var entry))
            _bySong[songId] = entry with { LastStatAtUtc = nowUtc };
    }
}

/// <summary>
/// The content-version token for a song's cover URLs (<c>?v=</c>). Derived only from row columns,
/// so it is stable within a list response and changes whenever a cover-affecting event changes the
/// row: a re-index/re-tag (LastModifiedUtc), a rebuild (LibraryBuiltAtUtc), a destination cover
/// written by the sweep (HasCoverArt), or a different destination folder. When the token in the
/// request matches, the cover endpoint may answer <c>immutable</c>.
/// </summary>
public static class CoverVersionStamp
{
    public static string ForSong(SongMetadata song) => ForSong(
        song.Id, song.LastModifiedUtc, song.LibraryBuiltAtUtc, song.HasCoverArt, song.DestinationPath);

    public static string ForSong(int id, DateTime lastModifiedUtc, DateTime? libraryBuiltAtUtc, bool hasCoverArt, string? destinationPath)
    {
        var material =
            $"{id}|{lastModifiedUtc.Ticks}|{libraryBuiltAtUtc?.Ticks ?? 0}|{hasCoverArt}|{destinationPath ?? ""}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..12].ToLowerInvariant();
    }
}
