using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Download;

public class StagedSourceReleaseServiceTests
{
    private const string Downloads = "/downloads";
    private const string Dest = "/dest";
    private const string SourcePath = "/downloads/track.flac";
    private const string DestinationPath = "/dest/Artist/2026 - Album/01 - Track.flac";

    [Fact]
    public async Task Release_DeletesVerifiedStagedSource_AndStampsRow()
    {
        var fs = Fs((SourcePath, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built());
        await db.SaveChangesAsync();
        var (service, tracker) = Create(db, fs);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Released);
        Assert.Equal(0, result.Failed);
        Assert.Equal(12, result.BytesReclaimed);
        Assert.False(fs.File.Exists(SourcePath));
        Assert.True(fs.File.Exists(DestinationPath));

        var row = await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.NotNull(row.SourceReleasedAtUtc);
        Assert.Equal(SourcePath, row.SourcePath);                 // the string identity survives
        Assert.Equal(DestinationPath, row.ReadableAudioPath);
        Assert.Equal(1, tracker.Get().Released);
    }

    [Fact]
    public async Task Release_PrunesEmptyStagingSubfolders_ButNeverTheStagingRoot()
    {
        const string nested = "/downloads/Artist/Album/track.flac";
        var fs = Fs((nested, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built(source: nested));
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs);

        await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.False(fs.Directory.Exists("/downloads/Artist/Album"));
        Assert.False(fs.Directory.Exists("/downloads/Artist"));
        Assert.True(fs.Directory.Exists(Downloads));
    }

    [Fact]
    public async Task Preview_CountsEligibleRowsAndBytes_WithoutTouchingAnything()
    {
        var fs = Fs((SourcePath, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built());
        db.Songs.Add(Built(source: "/downloads/other.flac", destination: "/dest/A/B/02.flac", released: true));
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs);

        var preview = await service.PreviewAsync(WellKnownUsers.OwnerId);

        Assert.Null(preview.UnavailableReason);
        Assert.Equal(1, preview.Eligible);
        Assert.Equal(12, preview.EligibleBytes);
        Assert.Equal(1, preview.Released);
        Assert.Equal(12, preview.ReleasedBytes);
        Assert.True(fs.File.Exists(SourcePath));
        Assert.Null((await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.SourcePath == SourcePath)).SourceReleasedAtUtc);
    }

    [Theory]
    [InlineData("grace-built")]
    [InlineData("grace-written")]
    [InlineData("not-download-root")]
    [InlineData("previous-destination-set")]
    [InlineData("not-done")]
    [InlineData("synthetic")]
    [InlineData("demo-tenant")]
    [InlineData("dest-equals-source")]
    [InlineData("already-released")]
    [InlineData("soft-deleted")]
    public async Task Release_SkipsIneligibleRows(string variant)
    {
        var fs = Fs((SourcePath, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"), ("/source/lib.flac", "audio-bytes-1"));
        await using var db = NewDb();
        var song = Built();
        switch (variant)
        {
            case "grace-built": song.LibraryBuiltAtUtc = DateTime.UtcNow; break;
            case "grace-written": song.LastWrittenAtUtc = DateTime.UtcNow; break;
            case "not-download-root": song.SourcePath = "/source/lib.flac"; break;
            case "previous-destination-set": song.PreviousDestinationPath = DestinationPath; break;
            case "not-done": song.LibraryBuildStatus = LibraryBuildStatus.Pending; break;
            case "synthetic": song.IsSynthetic = true; break;
            case "demo-tenant": song.OwnerUserId = WellKnownUsers.DemoId; break;
            case "dest-equals-source": song.DestinationPath = SourcePath; break;
            case "already-released": song.MarkSourceReleased(); break;
            case "soft-deleted": song.SoftDelete(); break;
        }
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs);

        var owner = variant == "demo-tenant" ? WellKnownUsers.DemoId : WellKnownUsers.OwnerId;
        var result = await service.ReleaseAsync(owner);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(0, result.Released);
        Assert.True(fs.File.Exists(song.SourcePath));
    }

    [Fact]
    public async Task Release_SkipsRowsUnderTheSyncedSourceRoot_InsideStaging()
    {
        const string synced = "/downloads/synced/track.flac";
        var fs = Fs((synced, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built(source: synced));
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs, sync: new SyncOptions
        {
            Mode = SyncMode.Receive,
            ApiKey = "k",
            SyncedSourceDirectory = "/downloads/synced",
        });

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(0, result.Candidates);
        Assert.True(fs.File.Exists(synced));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("unreadable")]
    [InlineData("duration-mismatch")]
    public async Task Release_LeavesSourceAndRowUntouched_WhenDestinationFailsVerification(string variant)
    {
        var destContent = variant == "truncated" ? "ab" : "audio-bytes-1";
        var fs = variant == "missing"
            ? Fs((SourcePath, "audio-bytes-1"))
            : Fs((SourcePath, "audio-bytes-1"), (DestinationPath, destContent));
        var probe = new FakeProbe(_ => variant switch
        {
            "unreadable" => null,
            "duration-mismatch" => new AudioProbeResult(120_000),
            _ => new AudioProbeResult(180_000),
        });
        await using var db = NewDb();
        db.Songs.Add(Built());
        await db.SaveChangesAsync();
        var (service, tracker) = Create(db, fs, probe);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.SkippedVerification);
        Assert.Equal(0, result.Released);
        Assert.True(fs.File.Exists(SourcePath));
        Assert.Null((await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync()).SourceReleasedAtUtc);
        Assert.Equal(1, tracker.Get().SkippedVerification);
    }

    [Fact]
    public async Task Release_AcceptsSmallSizeDifferenceFromRetagging()
    {
        // A re-tag rewrites only the tag block; the destination may be a little smaller than the source.
        var fs = Fs((SourcePath, new string('x', 100)), (DestinationPath, new string('x', 95)));
        await using var db = NewDb();
        db.Songs.Add(Built(size: 100));
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(1, result.Released);
    }

    [Fact]
    public async Task Release_RevertsStamp_WhenDeleteFails()
    {
        var inner = Fs((SourcePath, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"));
        var fs = new DeleteThrowingFileSystem(inner);
        await using var db = NewDb();
        db.Songs.Add(Built());
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Released);
        Assert.True(inner.File.Exists(SourcePath));
        Assert.Null((await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync()).SourceReleasedAtUtc);
    }

    [Fact]
    public async Task Release_KeepsStamp_WhenStagedFileWasAlreadyRemovedByHand()
    {
        // The stamp is exactly what saves such a row from the next scan's deletion sweep.
        var fs = Fs((DestinationPath, "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built());
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(1, result.AlreadyMissing);
        Assert.NotNull((await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync()).SourceReleasedAtUtc);
    }

    [Fact]
    public async Task Release_RevertsStamp_WhenRowWasResetUnderneath()
    {
        var fs = Fs((SourcePath, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"));
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = NewDb(dbName);
        db.Songs.Add(Built());
        await db.SaveChangesAsync();
        // Simulate a re-tag landing between the candidate query and the per-row stamp: the probe is
        // the last thing the service calls before loading the tracked row, so mutate the row there.
        var probe = new FakeProbe(_ =>
        {
            using var other = NewDb(dbName);
            var row = other.Songs.IgnoreQueryFilters().Single();
            row.RequeueForRetag();
            other.SaveChanges();
            return new AudioProbeResult(180_000);
        });
        var (service, _) = Create(db, fs, probe);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(1, result.Raced);
        Assert.Equal(0, result.Released);
        Assert.True(fs.File.Exists(SourcePath));
        var reloaded = await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Null(reloaded.SourceReleasedAtUtc);
        Assert.Equal(DestinationPath, reloaded.PreviousDestinationPath);
    }

    [Fact]
    public async Task Release_RefusesPathsOutsideTheManagedRoots()
    {
        // The eligibility query matches on a prefix; the per-row guard is boundary-safe and must veto
        // a destination that merely shares the root's prefix.
        const string outside = "/dest-old/Artist/01.flac";
        var fs = Fs((SourcePath, "audio-bytes-1"), (outside, "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built(destination: outside));
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(1, result.Failed);
        Assert.True(fs.File.Exists(SourcePath));
    }

    [Theory]
    [InlineData("downloads-disabled")]
    [InlineData("no-download-directory")]
    [InlineData("roots-nested")]
    public async Task Release_IsIdle_WhenTheFeatureCannotRunSafely(string reason)
    {
        var fs = Fs((SourcePath, "audio-bytes-1"), (DestinationPath, "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built());
        await db.SaveChangesAsync();
        var opts = Options(reason switch
        {
            "downloads-disabled" => o => o.EnableWishlistDownloads = false,
            "no-download-directory" => o => o.DownloadDirectory = "",
            _ => o => o.DestinationDirectory = "/downloads/library",
        });
        var (service, _) = Create(db, fs, options: opts);

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);
        var preview = await service.PreviewAsync(WellKnownUsers.OwnerId);

        Assert.Equal(reason, result.IdleReason);
        Assert.Equal(reason, preview.UnavailableReason);
        Assert.True(fs.File.Exists(SourcePath));
    }

    [Fact]
    public async Task Release_PagesPastRowsThatStayEligible()
    {
        // Two rows fail verification and stay eligible; with a batch size of 1 the keyset loop must
        // still terminate and reach the third, releasable row.
        var fs = Fs(
            ("/downloads/a.flac", "audio-bytes-1"),
            ("/downloads/b.flac", "audio-bytes-1"),
            ("/downloads/c.flac", "audio-bytes-1"), ("/dest/C/C/03.flac", "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built(source: "/downloads/a.flac", destination: "/dest/A/A/01.flac"));
        db.Songs.Add(Built(source: "/downloads/b.flac", destination: "/dest/B/B/02.flac"));
        db.Songs.Add(Built(source: "/downloads/c.flac", destination: "/dest/C/C/03.flac"));
        await db.SaveChangesAsync();
        var (service, _) = Create(db, fs, options: Options(o => o.StagedSourceReleaseBatchSize = 1));

        var result = await service.ReleaseAsync(WellKnownUsers.OwnerId);

        Assert.Equal(3, result.Candidates);
        Assert.Equal(2, result.SkippedVerification);
        Assert.Equal(1, result.Released);
        Assert.False(fs.File.Exists("/downloads/c.flac"));
    }

    [Fact]
    public async Task Release_StopsWhenCancelled()
    {
        var fs = Fs(
            ("/downloads/a.flac", "audio-bytes-1"), ("/dest/A/A/01.flac", "audio-bytes-1"),
            ("/downloads/b.flac", "audio-bytes-1"), ("/dest/B/B/02.flac", "audio-bytes-1"));
        await using var db = NewDb();
        db.Songs.Add(Built(source: "/downloads/a.flac", destination: "/dest/A/A/01.flac"));
        db.Songs.Add(Built(source: "/downloads/b.flac", destination: "/dest/B/B/02.flac"));
        await db.SaveChangesAsync();
        using var cts = new CancellationTokenSource();
        var probe = new FakeProbe(_ =>
        {
            cts.Cancel();
            return new AudioProbeResult(180_000);
        });
        var (service, _) = Create(db, fs, probe);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReleaseAsync(WellKnownUsers.OwnerId, cts.Token));

        Assert.True(fs.File.Exists("/downloads/b.flac"));
    }

    [Fact]
    public async Task Tracker_CancelAndWait_StopsARunningRelease()
    {
        var tracker = new StagedSourceReleaseTracker();
        Assert.True(tracker.TryStart("manual", out _, out var token));
        Assert.False(tracker.TryStart("sweep", out _, out _));
        Assert.True(tracker.IsRunning);

        var run = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { tracker.Cancelled(); }
        });

        await tracker.CancelAndWaitAsync();
        await run;

        Assert.False(tracker.IsRunning);
        Assert.Equal("cancelled", tracker.Get().Status);
        Assert.True(tracker.TryStart("sweep", out _, out _));
        tracker.Complete();
        Assert.Equal("completed", tracker.Get().Status);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SongMetadata Built(
        string source = SourcePath,
        string destination = DestinationPath,
        long size = 12,
        bool released = false)
    {
        var song = new SongMetadata
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = source,
            FileName = Path.GetFileName(source),
            Extension = Path.GetExtension(source),
            FileSizeBytes = size,
            LastModifiedUtc = DateTime.UtcNow.AddDays(-1),
            IndexedAtUtc = DateTime.UtcNow.AddDays(-1),
            DurationMs = 180_000,
            DurationSeconds = 180,
            Artist = "Artist",
            Album = "Album",
            Title = "Track",
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        song.MarkBuildDone(destination);
        song.LibraryBuiltAtUtc = DateTime.UtcNow.AddHours(-1);
        song.LastWrittenAtUtc = DateTime.UtcNow.AddHours(-1);
        if (released) song.MarkSourceReleased();
        return song;
    }

    private static MockFileSystem Fs(params (string Path, string Content)[] files) =>
        new(files.ToDictionary(f => f.Path, f => new MockFileData(f.Content)));

    private static IOptions<MusicEnricherOptions> Options(Action<MusicEnricherOptions>? mutate = null)
    {
        var o = new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = Dest,
            DownloadDirectory = Downloads,
            EnableWishlistDownloads = true,
            StagedSourceReleaseGraceMinutes = 15,
            StagedSourceReleaseBatchSize = 200,
        };
        mutate?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private static (StagedSourceReleaseService service, StagedSourceReleaseTracker tracker) Create(
        MusicHoarderDbContext db,
        IFileSystem fs,
        IAudioFileProbe? probe = null,
        IOptions<MusicEnricherOptions>? options = null,
        SyncOptions? sync = null)
    {
        var tracker = new StagedSourceReleaseTracker();
        var service = new StagedSourceReleaseService(
            db,
            fs,
            probe ?? new FakeProbe(_ => new AudioProbeResult(180_000)),
            options ?? Options(),
            new TestOptionsMonitor<SyncOptions>(sync ?? new SyncOptions()),
            tracker,
            NullLogger<StagedSourceReleaseService>.Instance);
        return (service, tracker);
    }

    /// <summary>A fresh in-memory database, or a second context over the same one when a name is given.</summary>
    private static MusicHoarderDbContext NewDb(string? name = null)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class FakeProbe(Func<string, AudioProbeResult?> probe) : IAudioFileProbe
    {
        public AudioProbeResult? Probe(string path) => probe(path);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Delegates everything to the inner mock filesystem except File.Delete, which throws.</summary>
    private sealed class DeleteThrowingFileSystem(MockFileSystem inner) : IFileSystem
    {
        public IFile File { get; } = new DeleteThrowingFile(inner);
        public IDirectory Directory => inner.Directory;
        public IFileInfoFactory FileInfo => inner.FileInfo;
        public IFileStreamFactory FileStream => inner.FileStream;
        public IPath Path => inner.Path;
        public IDirectoryInfoFactory DirectoryInfo => inner.DirectoryInfo;
        public IDriveInfoFactory DriveInfo => inner.DriveInfo;
        public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;
        public IFileVersionInfoFactory FileVersionInfo => inner.FileVersionInfo;

        private sealed class DeleteThrowingFile(MockFileSystem inner) : MockFile(inner)
        {
            public override void Delete(string path) => throw new IOException("simulated locked file");
        }
    }
}
