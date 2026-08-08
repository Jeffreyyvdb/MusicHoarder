using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// The acquisition intent is what "My music" filters on, so it must behave like the other user
/// signals (<c>LikedAtUtc</c>, play counts): survive every enrichment and build operation, and default
/// to "the owner wanted this" for anything that never went through the wishlist.
/// </summary>
public class SongAcquisitionIntentTests
{
    [Fact]
    public void NewSong_DefaultsToExplicit()
    {
        // The whole no-backfill story rests on this: Explicit is 0, so every pre-existing row and every
        // scanned or synced file is the owner's music without anyone writing to it.
        Assert.Equal(SongAcquisitionIntent.Explicit, NewSong().AcquisitionIntent);
        Assert.Equal(0, (int)SongAcquisitionIntent.Explicit);
    }

    [Fact]
    public void ResetEnrichment_DoesNotClearTheIntent()
    {
        var song = NewSong();
        song.AcquisitionIntent = SongAcquisitionIntent.AlbumFill;

        song.ResetEnrichment(restoreOriginal: true);

        Assert.Equal(SongAcquisitionIntent.AlbumFill, song.AcquisitionIntent);
    }

    [Fact]
    public void RequeueForRetag_DoesNotClearTheIntent()
    {
        var song = NewSong();
        song.AcquisitionIntent = SongAcquisitionIntent.AlbumFill;
        song.LibraryBuildStatus = LibraryBuildStatus.Done;
        song.DestinationPath = "/dest/Artist/Album/01 Track.flac";

        song.RequeueForRetag();

        Assert.Equal(SongAcquisitionIntent.AlbumFill, song.AcquisitionIntent);
    }

    [Fact]
    public void CaptureAndRestoreOriginalMetadata_DoNotTouchTheIntent()
    {
        var song = NewSong();
        song.AcquisitionIntent = SongAcquisitionIntent.AlbumFill;

        song.CaptureOriginalMetadata();
        song.RestoreOriginalMetadata();

        Assert.Equal(SongAcquisitionIntent.AlbumFill, song.AcquisitionIntent);
    }

    private static SongMetadata NewSong() => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = "/src/a.flac",
        FileName = "a.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Artist",
        Album = "Album",
        Title = "Title",
    };
}
