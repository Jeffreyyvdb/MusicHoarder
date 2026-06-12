using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

/// <summary>
/// IndexService ORs a sibling cover/folder/front.* image into <c>HasCoverArt</c> after the per-file
/// scan (the scanner itself only sees embedded art). IndexService enumerates the real filesystem,
/// so these tests use a real temp directory rather than MockFileSystem.
/// </summary>
public class IndexServiceTests : IDisposable
{
    private readonly string tempDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"mh-indextest-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Index_SetsHasCoverArt_FromSiblingFolderImage()
    {
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "track.mp3"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "cover.jpg"), [0xFF, 0xD8]);

        await using var db = NewContext();
        await CreateService(db).IndexAsync(Guid.NewGuid(), tempDir);

        var row = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.True(row.HasCoverArt);
    }

    [Fact]
    public async Task Index_LeavesHasCoverArtFalse_WithoutFolderImageOrEmbeddedArt()
    {
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "track.mp3"), [1, 2, 3]);

        await using var db = NewContext();
        await CreateService(db).IndexAsync(Guid.NewGuid(), tempDir);

        var row = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.False(row.HasCoverArt);
    }

    private static IndexService CreateService(MusicHoarderDbContext db) => new(
        new StubFileScanner(),
        db,
        new ScanProgressTracker(),
        new StubOwnerLookup(),
        new CoverArtResolver(new FileSystem(), new NoPictureReader()),
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        }),
        NullLogger<IndexService>.Instance);

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    // Returns metadata as the real scanner would for an untagged file with no embedded art.
    private sealed class StubFileScanner : IFileScanner
    {
        public Task<SongMetadata?> ScanFileAsync(string filePath, bool tagsOnly = false, CancellationToken ct = default)
            => Task.FromResult<SongMetadata?>(new SongMetadata
            {
                SourcePath = filePath,
                FileName = Path.GetFileName(filePath),
                Extension = Path.GetExtension(filePath),
                FileSizeBytes = 3,
                LastModifiedUtc = DateTime.UtcNow,
                IndexedAtUtc = DateTime.UtcNow,
                HasCoverArt = false,
            });
    }

    private sealed class StubOwnerLookup : IOwnerLookupService
    {
        public Guid OwnerUserId { get; } = WellKnownUsers.OwnerId;
    }

    private sealed class NoPictureReader : IEmbeddedPictureReader
    {
        public EmbeddedPicture? ReadFront(string filePath) => null;
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }
}
