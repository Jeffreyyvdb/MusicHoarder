using Microsoft.Extensions.Options;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class DestinationPathDotSegmentTests
{
    private static DestinationPathResolver CreateResolver() =>
        new(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest"
        }));

    [Theory]
    [InlineData("..", "..")]
    [InlineData("..", "victim")]
    [InlineData(".", ".")]
    [InlineData("...", "...")]
    [InlineData("Artist", "..")]
    public void ResolvePath_DotOnlyTagSegments_StayUnderDestinationRoot(string albumArtist, string album)
    {
        var song = new SongMetadata
        {
            SourcePath = "/source/t.mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            FileName = "t.mp3",
            Extension = ".mp3",
            Artist = albumArtist,
            AlbumArtist = albumArtist,
            Album = album,
            Title = "v06",
            TrackNumber = 1,
            Year = null
        };

        var resolved = Path.GetFullPath(CreateResolver().ResolvePath(song));

        Assert.StartsWith("/dest/", resolved, StringComparison.Ordinal);
    }
}
