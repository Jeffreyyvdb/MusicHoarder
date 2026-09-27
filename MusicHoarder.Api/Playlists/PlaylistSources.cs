using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Playlists;

/// <summary>What happens to a source-backed <see cref="Playlist"/> when its source is removed.</summary>
public static class PlaylistSources
{
    /// <summary>
    /// Prepares the playlist of a source about to be removed; the caller removes the source and saves.
    /// A playlist nobody added to goes with its source. One with songs added in MusicHoarder is work
    /// the owner put in, so it stays — as a MusicHoarder playlist, with the synced tracks it played
    /// frozen in ahead of the additions, so it plays exactly what it did.
    /// </summary>
    /// <returns>The exported file to delete once saved, if the playlist goes and had one.</returns>
    public static async Task<string?> DetachAsync(MusicHoarderDbContext db, WishlistSource source, CancellationToken ct)
    {
        var playlist = await db.Playlists
            .IgnoreQueryFilters()
            .Include(p => p.Entries)
            .FirstOrDefaultAsync(p => p.WishlistSourceId == source.Id && p.OwnerUserId == source.OwnerUserId, ct);
        if (playlist is null)
            return null;

        if (playlist.Entries.Count == 0)
        {
            db.Playlists.Remove(playlist);
            return playlist.ExportFilePath;
        }

        var refs = await PlaylistTracks.LoadRefsAsync(
            db, [playlist], new Dictionary<int, WishlistSource> { [source.Id] = source }, ct);
        var synced = refs[playlist.Id]
            .Where(r => r.FromSource && r.SongId is not null)
            .Select(r => r.SongId!.Value)
            .ToList();
        var playable = await PlaylistTracks.PlayableForOwnerAsync(db, source.OwnerUserId, synced, ct);

        var added = playlist.Entries.Select(e => e.SongId).ToHashSet();
        var frozen = synced
            .Where(playable.ContainsKey)
            .Select(id => playable[id])
            .Distinct()
            .Where(id => !added.Contains(id))
            .ToList();

        var now = DateTime.UtcNow;
        var position = 0;
        var additions = playlist.Entries.OrderBy(e => e.Position).ThenBy(e => e.Id).ToList();
        foreach (var songId in frozen)
            playlist.Entries.Add(new PlaylistEntry { SongId = songId, Position = position++, AddedAtUtc = now });
        foreach (var entry in additions)
            entry.Position = position++;

        if (!string.IsNullOrWhiteSpace(source.Name))
            playlist.Name = source.Name;
        playlist.WishlistSourceId = null;
        playlist.UpdatedAtUtc = now;
        return null;
    }
}
