using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Sharing;

/// <summary>
/// The published surface for a song you do not own, pinned field by field.
///
/// <para>
/// This test exists to fail. When someone adds a column to <see cref="SongMetadata"/> and helpfully
/// threads it through the shared projection, CI stops and makes them say out loud that they meant
/// to publish it to other people's accounts. Update the list only when that is the intent.
/// </para>
/// </summary>
public class SharedProjectionSurfaceTests
{
    /// <summary>Every property a grantee is allowed to receive. Order is irrelevant.</summary>
    private static readonly string[] Allowed =
    [
        "Id", "SourcePath", "FileName", "Extension", "FileSizeBytes",
        "Artist", "Artists", "AlbumArtist", "Album", "Title",
        "Year", "TrackNumber", "DiscNumber", "DurationSeconds", "DurationMs", "Bitrate",
        "Genre", "ReleaseDate", "OriginalReleaseDate", "Label",
        "MusicBrainzId", "MusicBrainzReleaseId", "Isrc", "SpotifyId",
        "HasCoverArt", "HasSyncedLyrics", "HasPlainLyrics", "IsInstrumental", "HasMusicVideo",
        "IndexedAtUtc", "AcquiredAtUtc",
        "IsBuilt", "SharedByUserId",
        "LikedAtUtc", "PlayCount", "LastPlayedAtUtc",
    ];

    [Fact]
    public void Shared_song_rows_publish_exactly_the_allowed_fields()
    {
        var actual = typeof(SharedSongRowDto)
            .GetProperties()
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Allowed.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }

    [Theory]
    // Filesystem layout: where the owner keeps their music, and how they name it.
    [InlineData("DestinationPath")]
    [InlineData("PreviousDestinationPath")]
    // Pipeline internals: nothing a listener can act on, plenty an attacker can profile.
    [InlineData("Fingerprint")]
    [InlineData("EnrichmentError")]
    [InlineData("LibraryBuildError")]
    [InlineData("MatchWarnings")]
    [InlineData("MatchedBy")]
    [InlineData("MatchConfidence")]
    [InlineData("EnrichmentStatus")]
    [InlineData("LibraryBuildStatus")]
    [InlineData("IsDuplicate")]
    [InlineData("DuplicateOfId")]
    [InlineData("OwnerUserId")]
    // The owner's pre-enrichment tags — their own filenames and typos.
    [InlineData("OriginalArtist")]
    [InlineData("OriginalAlbum")]
    [InlineData("OriginalTitle")]
    // The owner's personal Spotify save history. Nothing to do with what they shared.
    [InlineData("SpotifyAddedAtUtc")]
    [InlineData("SpotifyLikedAtUtc")]
    [InlineData("AcquisitionIntent")]
    [InlineData("OriginKind")]
    [InlineData("OriginSource")]
    [InlineData("OriginDetail")]
    public void Owner_only_fields_are_absent(string forbidden)
    {
        Assert.Null(typeof(SharedSongRowDto).GetProperty(forbidden));
    }

    [Fact]
    public void SourcePath_is_always_blank_never_the_real_path()
    {
        var dto = SharedSongRowDto.From(
            new SongMetadata
            {
                Id = 1,
                OwnerUserId = Guid.NewGuid(),
                SourcePath = "/mnt/nas/private/Music/Artist/Album/track.flac",
                FileName = "track.flac",
                Extension = ".flac",
                FileSizeBytes = 1,
                LastModifiedUtc = DateTime.UtcNow,
                IndexedAtUtc = DateTime.UtcNow,
                DestinationPath = "/mnt/nas/library/Artist/Album/track.flac",
            },
            sharedByUserId: Guid.NewGuid(),
            hasMusicVideo: false,
            state: null);

        Assert.Equal("", dto.SourcePath);
    }

    [Fact]
    public void IsBuilt_requires_both_a_done_status_and_a_destination()
    {
        // A client cannot derive this — granted rows carry no DestinationPath — so the server has
        // to be right about it, or a member's album view folds to "No built tracks yet".
        Assert.True(Dto(LibraryBuildStatus.Done, "/dest/a.flac").IsBuilt);
        Assert.False(Dto(LibraryBuildStatus.Done, null).IsBuilt);
        Assert.False(Dto(LibraryBuildStatus.Copied, "/dest/a.flac").IsBuilt);
        Assert.False(Dto(LibraryBuildStatus.Pending, null).IsBuilt);

        static SharedSongRowDto Dto(LibraryBuildStatus status, string? destination) =>
            SharedSongRowDto.From(
                new SongMetadata
                {
                    Id = 1,
                    OwnerUserId = Guid.NewGuid(),
                    SourcePath = "/src/a.flac",
                    FileName = "a.flac",
                    Extension = ".flac",
                    FileSizeBytes = 1,
                    LastModifiedUtc = DateTime.UtcNow,
                    IndexedAtUtc = DateTime.UtcNow,
                    LibraryBuildStatus = status,
                    DestinationPath = destination,
                },
                sharedByUserId: Guid.NewGuid(),
                hasMusicVideo: false,
                state: null);
    }
}
