using System.IO.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

/// <summary>
/// Covers the scanner's embedded-artwork detection. TagLib reads the real disk, so these use temp
/// copies of the committed silent fixtures with pictures seeded in-test.
/// </summary>
public class FileScannerTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly List<string> tempFiles = [];

    [Fact]
    public async Task ScanFile_SetsHasCoverArt_WhenEmbeddedPicturePresent()
    {
        var path = CopyFixture("silence.mp3");
        using (var seed = TagLib.File.Create(path))
        {
            seed.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }))
                {
                    MimeType = "image/png",
                    Type = TagLib.PictureType.FrontCover,
                }
            ];
            seed.Save();
        }

        var metadata = await CreateScanner().ScanFileAsync(path, tagsOnly: true);

        Assert.NotNull(metadata);
        Assert.True(metadata!.HasCoverArt);
    }

    [Fact]
    public async Task ScanFile_LeavesHasCoverArtFalse_WhenNoEmbeddedPicture()
    {
        var path = CopyFixture("silence.mp3");

        var metadata = await CreateScanner().ScanFileAsync(path, tagsOnly: true);

        Assert.NotNull(metadata);
        Assert.False(metadata!.HasCoverArt);
    }

    [Fact]
    public async Task ScanFile_SeedsAcquiredAt_FromFileMtime_WhenOlderThanTheScan()
    {
        // A file that's been sitting on disk for years is acquired then, not at first scan — otherwise
        // an initial bulk import collapses the whole back catalogue into one instant.
        var path = CopyFixture("silence.mp3");
        var mtime = new DateTime(2019, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, mtime);

        var metadata = await CreateScanner().ScanFileAsync(path, tagsOnly: true);

        Assert.NotNull(metadata);
        Assert.Equal(mtime, metadata!.AcquiredAtUtc);
        Assert.True(metadata.IndexedAtUtc > mtime);
    }

    [Fact]
    public async Task ScanFile_SeedsAcquiredAt_FromScanTime_WhenMtimeIsInTheFuture()
    {
        var path = CopyFixture("silence.mp3");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(30));

        var metadata = await CreateScanner().ScanFileAsync(path, tagsOnly: true);

        Assert.NotNull(metadata);
        Assert.Equal(metadata!.IndexedAtUtc, metadata.AcquiredAtUtc);
    }

    private static FileScanner CreateScanner() =>
        new(new FileSystem(), new NullFpcalcService(), NullLogger<FileScanner>.Instance);

    private string CopyFixture(string fixtureName)
    {
        var source = Path.Combine(FixtureDir, fixtureName);
        var dest = Path.Combine(
            Path.GetTempPath(),
            $"mh-scantest-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
        File.Copy(source, dest, overwrite: true);
        tempFiles.Add(dest);
        return dest;
    }

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    // tagsOnly scans never call fpcalc; this just satisfies the dependency.
    private sealed class NullFpcalcService : IFpcalcService
    {
        public Task<FpcalcOutcome> GetFingerprintAsync(string filePath, int? maxLengthSeconds = null, CancellationToken ct = default) =>
            Task.FromResult(FpcalcOutcome.Failure("test"));
    }
}
