using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Flips <see cref="SongMetadata.HasCoverArt"/> for tracks whose <em>destination</em> album folder has
/// a cover image on disk. The scanner/index only ever set the flag from the <em>source</em> side, so an
/// art-less album that got its <c>cover.&lt;ext&gt;</c> from an external fetch (written to the destination)
/// would otherwise keep <c>HasCoverArt = false</c> and never request the (destination-aware) cover
/// endpoint. Broadening the flag to mean "a cover is resolvable for this song (source or destination)"
/// lets the existing <c>/songs/{id}/cover</c> endpoint + thumbnail pipeline surface fetched covers.
/// </summary>
internal static class DestinationCoverFlagger
{
    /// <summary>
    /// Sets <c>HasCoverArt = true</c> for the non-deleted, non-synthetic, non-demo songs whose
    /// <see cref="SongMetadata.DestinationPath"/> lives directly in <paramref name="folder"/>. Tracked
    /// load + <c>SaveChanges</c> (the EF in-memory test provider can't translate <c>ExecuteUpdate</c>);
    /// only rows currently <c>false</c> are touched, so it's idempotent. Returns the number flagged.
    /// </summary>
    internal static async Task<int> FlagFolderAsync(
        MusicHoarderDbContext db, string folder, char separator, CancellationToken ct)
    {
        // Album folders are flat (folder/<track>.<ext>); the separator-suffixed prefix keeps
        // "/music/Album" from matching a sibling "/music/Album (Deluxe)".
        var prefix = folder.EndsWith(separator) ? folder : folder + separator;

        var songs = await db.Songs
            .IgnoreQueryFilters()
            .ExcludingDemoTenant()
            .Where(s => !s.HasCoverArt
                && s.DeletedAtUtc == null
                && !s.IsSynthetic
                && s.DestinationPath != null
                && s.DestinationPath.StartsWith(prefix))
            .ToListAsync(ct);

        foreach (var song in songs)
        {
            song.HasCoverArt = true;
        }

        if (songs.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return songs.Count;
    }
}
