using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class SongMediaEndpointsTests : IDisposable
{
    // ── temp directory ────────────────────────────────────────────────────────
    private readonly DirectoryInfo _tmpDir = Directory.CreateTempSubdirectory("mh-test-");

    public void Dispose()
    {
        try { _tmpDir.Delete(recursive: true); }
        catch { /* best-effort */ }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static SongMetadata NewSong(string sourcePath, string fileName) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    /// <summary>Writes a tiny stub file and returns its full path.</summary>
    private string TempFile(string name)
    {
        var path = Path.Combine(_tmpDir.FullName, name);
        File.WriteAllBytes(path, new byte[] { 0x00 });
        return path;
    }

    // ── fakes ─────────────────────────────────────────────────────────────────
    private sealed class FakeCoverArtResolver(ResolvedCover? cover) : ICoverArtResolver
    {
        public bool ResolveCalled { get; private set; }

        public ResolvedCover? Resolve(string audioFilePath)
        {
            ResolveCalled = true;
            return cover;
        }

        public bool DirectoryHasCoverImage(string? directory) => false;
    }

    private sealed class FakeThumbnailService(ResolvedCover? thumb) : ICoverThumbnailService
    {
        public int? LastRequestedSize { get; private set; }

        public int ClampToBucket(int requestedSize) => requestedSize;

        public Task<ResolvedCover?> GetThumbnailAsync(
            ResolvedCover source, string identityPath, int size, CancellationToken ct = default)
        {
            LastRequestedSize = size;
            return Task.FromResult(thumb);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  StreamSong
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StreamSong_UnknownId_Returns404()
    {
        await using var db = NewContext();
        // no songs added

        var result = await SongsEndpoints.StreamSong(999, db);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task StreamSong_SoftDeleted_Returns404()
    {
        await using var db = NewContext();
        var song = NewSong("/absent.mp3", "absent.mp3");
        song.DeletedAtUtc = DateTime.UtcNow;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.StreamSong(song.Id, db);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task StreamSong_SourceFileExists_StreamsWithCorrectMime()
    {
        var sourcePath = TempFile("song.mp3");

        await using var db = NewContext();
        var song = NewSong(sourcePath, "song.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.StreamSong(song.Id, db);

        var streamResult = Assert.IsType<FileStreamHttpResult>(result);
        Assert.Equal("audio/mpeg", streamResult.ContentType);
        Assert.True(streamResult.EnableRangeProcessing);
        Assert.Equal(sourcePath, ((FileStream)streamResult.FileStream).Name);

        // Must close the stream before Dispose() tries to delete the temp dir.
        await streamResult.FileStream.DisposeAsync();
    }

    [Fact]
    public async Task StreamSong_SourceAbsent_DestinationExists_StreamsDestination()
    {
        var destPath = TempFile("song.flac");

        await using var db = NewContext();
        var song = NewSong("/nonexistent/source.flac", "song.flac");
        song.DestinationPath = destPath;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.StreamSong(song.Id, db);

        var streamResult = Assert.IsType<FileStreamHttpResult>(result);
        Assert.Equal("audio/flac", streamResult.ContentType);
        Assert.True(streamResult.EnableRangeProcessing);
        Assert.Equal(destPath, ((FileStream)streamResult.FileStream).Name);

        await streamResult.FileStream.DisposeAsync();
    }

    [Fact]
    public async Task StreamSong_BothPathsAbsent_Returns404()
    {
        await using var db = NewContext();
        var song = NewSong("/nonexistent/source.mp3", "song.mp3");
        song.DestinationPath = "/nonexistent/dest.mp3";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.StreamSong(song.Id, db);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task StreamSong_UnknownExtension_UsesOctetStream()
    {
        var sourcePath = TempFile("audio.xyz");

        await using var db = NewContext();
        var song = NewSong(sourcePath, "audio.xyz");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await SongsEndpoints.StreamSong(song.Id, db);

        var streamResult = Assert.IsType<FileStreamHttpResult>(result);
        Assert.Equal("application/octet-stream", streamResult.ContentType);

        await streamResult.FileStream.DisposeAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GetSongCover
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSongCover_UnknownId_Returns404()
    {
        await using var db = NewContext();
        var resolver = new FakeCoverArtResolver(null);
        var thumbs = new FakeThumbnailService(null);
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(999, db, resolver, thumbs, http, null);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task GetSongCover_SyntheticRow_Returns404_WithoutCallingResolver()
    {
        var sourcePath = TempFile("synth.mp3");

        await using var db = NewContext();
        var song = NewSong(sourcePath, "synth.mp3");
        song.IsSynthetic = true;
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var resolver = new FakeCoverArtResolver(new ResolvedCover { FilePath = sourcePath, ContentType = "image/jpeg" });
        var thumbs = new FakeThumbnailService(null);
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(song.Id, db, resolver, thumbs, http, null);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(resolver.ResolveCalled, "Resolver should NOT be called for synthetic rows.");
    }

    [Fact]
    public async Task GetSongCover_NoFileOnDisk_Returns404_WithoutCallingResolver()
    {
        await using var db = NewContext();
        var song = NewSong("/nonexistent/song.mp3", "song.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var resolver = new FakeCoverArtResolver(new ResolvedCover { FilePath = "/some/cover.jpg", ContentType = "image/jpeg" });
        var thumbs = new FakeThumbnailService(null);
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(song.Id, db, resolver, thumbs, http, null);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(resolver.ResolveCalled, "Resolver should NOT be called when the audio file is missing.");
    }

    [Fact]
    public async Task GetSongCover_ResolverReturnsNull_Returns404()
    {
        var sourcePath = TempFile("song.mp3");

        await using var db = NewContext();
        var song = NewSong(sourcePath, "song.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var resolver = new FakeCoverArtResolver(null);
        var thumbs = new FakeThumbnailService(null);
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(song.Id, db, resolver, thumbs, http, null);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.True(resolver.ResolveCalled);
    }

    [Fact]
    public async Task GetSongCover_FileCover_NoSize_ReturnsPhysicalFile_WithShortCache()
    {
        var sourcePath = TempFile("song.mp3");
        var coverPath = TempFile("cover.jpg");

        await using var db = NewContext();
        var song = NewSong(sourcePath, "song.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var cover = new ResolvedCover { FilePath = coverPath, ContentType = "image/jpeg" };
        var resolver = new FakeCoverArtResolver(cover);
        var thumbs = new FakeThumbnailService(null);
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(song.Id, db, resolver, thumbs, http, null);

        var fileResult = Assert.IsType<PhysicalFileHttpResult>(result);
        Assert.Equal(coverPath, fileResult.FileName);
        Assert.Equal("private, max-age=86400", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetSongCover_BytesCover_NoSize_ReturnsFileContent_WithShortCache()
    {
        var sourcePath = TempFile("song.mp3");
        var coverBytes = new byte[] { 0xFF, 0xD8, 0xFF }; // JPEG magic bytes

        await using var db = NewContext();
        var song = NewSong(sourcePath, "song.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var cover = new ResolvedCover { Bytes = coverBytes, ContentType = "image/jpeg" };
        var resolver = new FakeCoverArtResolver(cover);
        var thumbs = new FakeThumbnailService(null);
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(song.Id, db, resolver, thumbs, http, null);

        var bytesResult = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal(coverBytes, bytesResult.FileContents.ToArray());
        Assert.Equal("private, max-age=86400", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetSongCover_WithSize_ThumbServiceReturnsFile_ReturnsThumbWithLongCache()
    {
        var sourcePath = TempFile("song.mp3");
        var thumbPath = TempFile("thumb.webp");

        await using var db = NewContext();
        var song = NewSong(sourcePath, "song.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var cover = new ResolvedCover { FilePath = thumbPath, ContentType = "image/jpeg" };
        var thumb = new ResolvedCover { FilePath = thumbPath, ContentType = "image/webp" };
        var resolver = new FakeCoverArtResolver(cover);
        var thumbs = new FakeThumbnailService(thumb);
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(song.Id, db, resolver, thumbs, http, 256);

        var fileResult = Assert.IsType<PhysicalFileHttpResult>(result);
        Assert.Equal(thumbPath, fileResult.FileName);
        Assert.Equal("private, max-age=604800", http.Response.Headers.CacheControl.ToString());
        Assert.Equal(256, thumbs.LastRequestedSize);
    }

    [Fact]
    public async Task GetSongCover_WithSize_ThumbServiceReturnsNull_FallsBackToOriginalWithShortCache()
    {
        var sourcePath = TempFile("song.mp3");
        var coverPath = TempFile("cover.jpg");

        await using var db = NewContext();
        var song = NewSong(sourcePath, "song.mp3");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var cover = new ResolvedCover { FilePath = coverPath, ContentType = "image/jpeg" };
        var resolver = new FakeCoverArtResolver(cover);
        var thumbs = new FakeThumbnailService(null); // thumbnailing fails
        var http = new DefaultHttpContext();

        var result = await SongsEndpoints.GetSongCover(song.Id, db, resolver, thumbs, http, 256);

        var fileResult = Assert.IsType<PhysicalFileHttpResult>(result);
        Assert.Equal(coverPath, fileResult.FileName);
        Assert.Equal("private, max-age=86400", http.Response.Headers.CacheControl.ToString());
    }
}
