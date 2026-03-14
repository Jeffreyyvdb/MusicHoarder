using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

public interface IDestinationPathResolver
{
    string ResolvePath(SongMetadata song);
}
