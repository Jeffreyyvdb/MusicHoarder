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

    [Fact]
    public async Task Index_SkipsDotFilesAndDotDirectories()
    {
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "track.mp3"), [1, 2, 3]);
        var incoming = Directory.CreateDirectory(Path.Combine(tempDir, ".incoming")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(incoming, "halfwritten.flac"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "._resourcefork.mp3"), [1, 2, 3]);

        await using var db = NewContext();
        var result = await CreateService(db).IndexAsync(Guid.NewGuid(), tempDir);

        Assert.Equal(1, result.TotalFiles);
        var row = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.EndsWith("track.mp3", row.SourcePath);
    }

    [Theory]
    [InlineData("/root/.incoming/file.flac", "/root/", true)]
    [InlineData("/root/sub/.hidden/file.flac", "/root/", true)]
    [InlineData("/root/._file.mp3", "/root/", true)]
    [InlineData("/root/Artist/file.flac", "/root/", false)]
    [InlineData("/srv/.media/music/file.flac", "/srv/.media/music/", false)] // dot in the ROOT doesn't count
    public void HasHiddenSegment_OnlyFlagsSegmentsBelowRoot(string file, string root, bool expected)
    {
        Assert.Equal(expected, IndexService.HasHiddenSegment(file, root));
    }

    [Fact]
    public async Task Index_ScopesDeletionToScannedRoot_LeavingOtherRootsUntouched()
    {
        // Two source roots: the read-only library (rootA) and the writable download dir (rootB).
        // Scanning rootB must reconcile deletions only within rootB — songs under rootA must survive
        // even though they aren't discovered by this scan (otherwise an offline source root, or a
        // download-only scan, would wipe the whole library).
        var rootA = Directory.CreateDirectory(Path.Combine(tempDir, "rootA")).FullName;
        var rootB = Directory.CreateDirectory(Path.Combine(tempDir, "rootB")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(rootB, "present.mp3"), [1, 2, 3]);

        await using var db = NewContext();
        // A library song under rootA (not part of this scan) and a now-missing song under rootB.
        db.Songs.Add(Seed(Path.Combine(rootA, "library.mp3").Replace('\\', '/')));
        db.Songs.Add(Seed(Path.Combine(rootB, "gone.mp3").Replace('\\', '/')));
        await db.SaveChangesAsync();

        await CreateService(db).IndexAsync(Guid.NewGuid(), rootB);

        var rows = await db.Songs.IgnoreQueryFilters().ToListAsync();
        var libraryRow = rows.Single(r => r.SourcePath.EndsWith("library.mp3"));
        var goneRow = rows.Single(r => r.SourcePath.EndsWith("gone.mp3"));

        Assert.Null(libraryRow.DeletedAtUtc);       // different root — untouched
        Assert.NotNull(goneRow.DeletedAtUtc);        // scanned root, missing on disk — soft-deleted
        Assert.Contains(rows, r => r.SourcePath.EndsWith("present.mp3")); // newly discovered
    }

    private static SongMetadata Seed(string sourcePath) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 3,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Index_DefersFileStillBeingWritten()
    {
        // A file touched just now looks like a copy in flight — indexing it would read truncated tags
        // and burn an enrichment attempt before the next scan noticed the size changed.
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "copying.mp3"), [1, 2, 3]);

        await using var db = NewContext();
        var result = await CreateService(db, settleSeconds: 300, sourceDirectory: tempDir).IndexAsync(Guid.NewGuid(), tempDir);

        Assert.Equal(0, result.NewFiles);
        Assert.Empty(await db.Songs.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Index_PicksUpFileOnceItHasSettled()
    {
        var path = Path.Combine(tempDir, "settled.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));

        await using var db = NewContext();
        var result = await CreateService(db, settleSeconds: 300, sourceDirectory: tempDir).IndexAsync(Guid.NewGuid(), tempDir);

        Assert.Equal(1, result.NewFiles);
        Assert.Single(await db.Songs.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Index_DoesNotSoftDeleteExistingRowWhileItsFileIsBeingRewritten()
    {
        // Deferred files must still count as "discovered" — otherwise deletion reconciliation would
        // soft-delete the row for a file that is merely being rewritten in place.
        var path = Path.Combine(tempDir, "rewriting.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));

        await using var db = NewContext();
        await CreateService(db, settleSeconds: 300, sourceDirectory: tempDir).IndexAsync(Guid.NewGuid(), tempDir);

        // Now it's being rewritten: mtime moves to "just now", so the next scan defers it.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        await CreateService(db, settleSeconds: 300, sourceDirectory: tempDir).IndexAsync(Guid.NewGuid(), tempDir);

        var row = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.Null(row.DeletedAtUtc);
    }

    [Fact]
    public async Task Index_DoesNotDeferFilesOutsideTheSourceRoot()
    {
        // The download-staging and sync-receive roots are app-managed: a finished download triggers a
        // scan immediately, carrying a one-shot force-cascade flag. Deferring it would consume that
        // flag and strand the freshly acquired track outside the library.
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "justdownloaded.mp3"), [1, 2, 3]);

        await using var db = NewContext();
        var result = await CreateService(db, settleSeconds: 300, sourceDirectory: "/some/other/source")
            .IndexAsync(Guid.NewGuid(), tempDir);

        Assert.Equal(1, result.NewFiles);
    }

    [Fact]
    public async Task Index_SettleWindowDisabledByZero()
    {
        await File.WriteAllBytesAsync(Path.Combine(tempDir, "fresh.mp3"), [1, 2, 3]);

        await using var db = NewContext();
        var result = await CreateService(db, settleSeconds: 0, sourceDirectory: tempDir).IndexAsync(Guid.NewGuid(), tempDir);

        Assert.Equal(1, result.NewFiles);
    }

    // settleSeconds defaults to 0 (guard off) because these tests write a file and index it in the
    // same breath; the settle window is exercised explicitly by the tests that pass a value. It only
    // applies to the configured source root, so those tests also point sourceDirectory at the temp dir.
    private static IndexService CreateService(
        MusicHoarderDbContext db, int settleSeconds = 0, string sourceDirectory = "/source") => new(
        new StubFileScanner(),
        db,
        new ScanProgressTracker(),
        new StubOwnerLookup(),
        new CoverArtResolver(new FileSystem(), new NoPictureReader()),
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = sourceDirectory,
            DestinationDirectory = "/dest",
            ScanSettleSeconds = settleSeconds,
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
