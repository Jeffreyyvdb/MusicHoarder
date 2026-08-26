using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Endpoints;

public class MusicVideoEndpointsTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static SongMetadata Song(int id) => new()
    {
        Id = id,
        OwnerUserId = Owner,
        SourcePath = $"/src/{id}.opus",
        FileName = $"{id}.opus",
        Extension = ".opus",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Artist",
        Title = "Title",
    };

    private static SongMusicVideo ReadyVideo(int songId, int offsetMs = 0) => new()
    {
        SongId = songId,
        FilePath = "/videos/clip.mp4",
        YouTubeVideoId = "vid-1",
        Status = MusicVideoStatus.Ready,
        SyncSource = MusicVideoSyncSource.AutoAligned,
        SyncOffsetMs = offsetMs,
        SyncConfidence = 0.93,
        FetchedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetVideoInfo_NoRow_Returns404()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.GetVideoInfo(1, db, TestLibraryScope.For(TestUsers.OwnerId), default);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task GetVideoInfo_ReturnsSyncFields()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        db.SongMusicVideos.Add(ReadyVideo(1, offsetMs: 2400));
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.GetVideoInfo(1, db, TestLibraryScope.For(TestUsers.OwnerId), default);
        var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoInfoDto>>(result);
        Assert.Equal("Ready", ok.Value!.Status);
        Assert.Equal(2400, ok.Value.SyncOffsetMs);
        Assert.Equal("AutoAligned", ok.Value.SyncSource);
        Assert.Equal(0.93, ok.Value.SyncConfidence);
    }

    [Fact]
    public async Task GetVideoInfo_ReadyRowWithMissingFile_FlagsFileMissing()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        db.SongMusicVideos.Add(ReadyVideo(1)); // FilePath points nowhere on disk
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.GetVideoInfo(1, db, TestLibraryScope.For(TestUsers.OwnerId), default);
        var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoInfoDto>>(result);
        Assert.Equal("Ready", ok.Value!.Status);
        Assert.True(ok.Value.FileMissing);
    }

    [Fact]
    public async Task GetVideoInfo_ReadyRowWithExistingFile_DoesNotFlagFileMissing()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mh-video-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tempFile, [0x00]);
        try
        {
            await using var db = NewContext();
            db.Songs.Add(Song(1));
            var video = ReadyVideo(1);
            video.FilePath = tempFile;
            db.SongMusicVideos.Add(video);
            await db.SaveChangesAsync();

            var result = await MusicVideoEndpoints.GetVideoInfo(1, db, TestLibraryScope.For(TestUsers.OwnerId), default);
            var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoInfoDto>>(result);
            Assert.False(ok.Value!.FileMissing);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetVideoInfo_SoftDeletedSong_Returns404()
    {
        await using var db = NewContext();
        var song = Song(1);
        song.SoftDelete();
        db.Songs.Add(song);
        db.SongMusicVideos.Add(ReadyVideo(1));
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.GetVideoInfo(1, db, TestLibraryScope.For(TestUsers.OwnerId), default);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task StreamVideo_MissingFile_Returns404()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        db.SongMusicVideos.Add(ReadyVideo(1)); // FilePath points nowhere on disk
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.StreamVideo(1, db, TestLibraryScope.For(TestUsers.OwnerId), default);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task FetchVideo_UnknownSong_Returns404()
    {
        await using var db = NewContext();
        var result = await MusicVideoEndpoints.FetchVideo(99, null, db, new MusicVideoChannel(), default);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task FetchVideo_InvalidUrl_Returns400()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.FetchVideo(
            1, new MusicVideoEndpoints.FetchVideoRequest("https://vimeo.com/12345"), db, new MusicVideoChannel(), default);
        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task FetchVideo_CreatesFetchingRow_AndEnqueuesPinnedUrl()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        await db.SaveChangesAsync();

        var channel = new MusicVideoChannel();
        var result = await MusicVideoEndpoints.FetchVideo(
            1, new MusicVideoEndpoints.FetchVideoRequest("https://youtu.be/dQw4w9WgXcQ"), db, channel, default);

        Assert.Equal(StatusCodes.Status202Accepted, ((IStatusCodeHttpResult)result).StatusCode);
        var row = await db.SongMusicVideos.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(MusicVideoStatus.Fetching, row.Status);
        Assert.Equal("dQw4w9WgXcQ", row.YouTubeVideoId); // pinned id survives a restart's re-enqueue
        Assert.True(channel.Reader.TryRead(out var work));
        Assert.Equal(
            new MusicVideoWorkItem(1, MusicVideoWorkKind.Fetch, "https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
            work);
    }

    [Fact]
    public async Task FetchVideo_AlreadyFetching_DoesNotStackASecondFetch()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        db.SongMusicVideos.Add(new SongMusicVideo { SongId = 1, Status = MusicVideoStatus.Fetching });
        await db.SaveChangesAsync();

        var channel = new MusicVideoChannel();
        var result = await MusicVideoEndpoints.FetchVideo(1, null, db, channel, default);

        Assert.Equal(StatusCodes.Status202Accepted, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task SetVideoOffset_ClampsAndMarksManual()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        db.SongMusicVideos.Add(ReadyVideo(1));
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.SetVideoOffset(
            1, new MusicVideoEndpoints.SetOffsetRequest(99_000_000, null), db, new MusicVideoChannel(), default);

        var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoInfoDto>>(result);
        Assert.Equal(600_000, ok.Value!.SyncOffsetMs); // clamped to ±10 min
        Assert.Equal("Manual", ok.Value.SyncSource);
        Assert.Null(ok.Value.SyncConfidence);
    }

    [Fact]
    public async Task SetVideoOffset_ResetToAuto_ClearsOffsetAndEnqueuesAlignment()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        db.SongMusicVideos.Add(ReadyVideo(1, offsetMs: 1800));
        await db.SaveChangesAsync();

        var channel = new MusicVideoChannel();
        var result = await MusicVideoEndpoints.SetVideoOffset(
            1, new MusicVideoEndpoints.SetOffsetRequest(null, true), db, channel, default);

        var ok = Assert.IsType<Ok<MusicVideoEndpoints.VideoInfoDto>>(result);
        Assert.Equal(0, ok.Value!.SyncOffsetMs);
        Assert.Equal("Unaligned", ok.Value.SyncSource);
        Assert.True(channel.Reader.TryRead(out var work));
        Assert.Equal(new MusicVideoWorkItem(1, MusicVideoWorkKind.Align), work);
    }

    [Fact]
    public async Task SetVideoOffset_NoBodyValues_Returns400()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(1));
        db.SongMusicVideos.Add(ReadyVideo(1));
        await db.SaveChangesAsync();

        var result = await MusicVideoEndpoints.SetVideoOffset(
            1, new MusicVideoEndpoints.SetOffsetRequest(null, null), db, new MusicVideoChannel(), default);
        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task DeleteVideo_RemovesRowAndFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mh-video-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(tempFile, [0x00]);
        try
        {
            await using var db = NewContext();
            db.Songs.Add(Song(1));
            var video = ReadyVideo(1);
            video.FilePath = tempFile;
            db.SongMusicVideos.Add(video);
            await db.SaveChangesAsync();

            var result = await MusicVideoEndpoints.DeleteVideo(1, db, default);

            Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)result).StatusCode);
            Assert.Empty(await db.SongMusicVideos.IgnoreQueryFilters().ToListAsync());
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
