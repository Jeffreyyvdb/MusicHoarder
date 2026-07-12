using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

public interface IDestinationPathResolver
{
    string ResolvePath(SongMetadata song);

    /// <summary>
    /// Resolves the destination path deriving the album-IDENTITY folder segments from the reconciled
    /// <paramref name="albumIdentity"/> (its album-artist / album / year / compilation flag) when one is
    /// supplied — making an album's folder deterministic across build runs — and the track-level file
    /// name from the song. Passing <c>null</c> is identical to <see cref="ResolvePath(SongMetadata)"/>.
    /// Defaults to ignoring the identity (per-song routing) so test doubles need only the single-arg form;
    /// the production resolver overrides this to honour the elected identity.
    /// </summary>
    string ResolvePath(SongMetadata song, AlbumIdentity? albumIdentity) => ResolvePath(song);
}
