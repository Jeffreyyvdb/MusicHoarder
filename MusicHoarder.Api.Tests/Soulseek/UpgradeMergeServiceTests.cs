using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Soulseek;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Soulseek;

public class UpgradeMergeServiceTests : IDisposable
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private readonly string stagingDir;

    public UpgradeMergeServiceTests()
    {
        stagingDir = Path.Combine(Path.GetTempPath(), $"mh-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(stagingDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Merge_SwapsSourcePreservingIdEnrichmentAndLyrics()
    {
        await using var db = CreateDbContext();
        var oldSource = WriteStagingFile("old.opus");
        var target = TargetSong(10, oldSource, extension: ".opus", bitrate: 128, fingerprint: "FP-old");
        target.PlainLyrics = "the lyrics";
        target.SyncedLyrics = "[00:01.00] the lyrics";
        target.LyricsStatus = LyricsStatus.Fetched;
        target.MarkBuildDone("/dest/Artist/song.opus");
        var newFile = WriteStagingFile("better.flac", bytes: 2048);
        var provisional = ProvisionalSong(11, newFile, extension: ".flac", bitrate: 900, fingerprint: "FP-new");
        db.Songs.AddRange(target, provisional);
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
        await db.SaveChangesAsync();

        var merged = await CreateService(db).SweepAsync(default);

        Assert.Equal(1, merged);
        var songs = await db.Songs.IgnoreQueryFilters().ToListAsync();
        var song = Assert.Single(songs); // provisional row hard-deleted
        Assert.Equal(10, song.Id);       // same row, Id preserved
        Assert.Equal(newFile.Replace('\\', '/'), song.SourcePath);
        Assert.Equal(".flac", song.Extension);
        Assert.Equal(900, song.Bitrate);
        Assert.Equal("FP-new", song.Fingerprint);
        // Enrichment identity + lyrics untouched.
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.Equal("Artist", song.Artist);
        Assert.Equal("the lyrics", song.PlainLyrics);
        Assert.Equal(LyricsStatus.Fetched, song.LyricsStatus);
        // Destination swap armed.
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Equal("/dest/Artist/song.opus", song.PreviousDestinationPath);
        Assert.Null(song.DestinationPath);
        // Old managed source file removed; new file still present.
        Assert.False(File.Exists(oldSource));
        Assert.True(File.Exists(newFile));

        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Completed, request.Status);
    }

    [Fact]
    public async Task Merge_ProcessesOwnerRows_UnderBackgroundTenantFilter()
    {
        // Regression: in production the DbContext always has an ICurrentUserAccessor, so a background
        // scope (no HTTP user) resolves the tenant filter to Guid.Empty — hiding the owner's rows
        // unless the service uses IgnoreQueryFilters. This is the exact condition under which the
        // upgrade merge sweep silently found nothing and upgrades stalled forever. A shared-name
        // in-memory DB lets us seed/assert with a permissive context while the SERVICE runs against
        // the restrictive, prod-like one.
        var dbName = Guid.NewGuid().ToString("N");
        var opts = new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(dbName).Options;

        // Seed as the OWNER (not anonymous): the EF model cache keys on userId, so a no-accessor
        // seed would cache a *permissive* model under Guid.Empty that the background context (also
        // Empty) would then reuse — masking the bug. In prod the accessor is always present, so no
        // permissive model is ever cached under Empty.
        var newFile = WriteStagingFile("better.flac", bytes: 2048);
        await using (var seed = new MusicHoarderDbContext(opts, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)))
        {
            var target = TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128, fingerprint: "FP-old");
            target.MarkBuildDone("/dest/Artist/song.opus");
            seed.Songs.AddRange(target, ProvisionalSong(11, newFile, extension: ".flac", bitrate: 900, fingerprint: "FP-new"));
            seed.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
            await seed.SaveChangesAsync();
        }

        // Service runs against a context whose tenant filter resolves to Guid.Empty (prod background scope).
        await using (var backgroundDb = new MusicHoarderDbContext(opts, new AnonymousUserAccessor()))
        {
            var merged = await CreateService(backgroundDb).SweepAsync(default);
            Assert.Equal(1, merged); // would be 0 without IgnoreQueryFilters — the bug
        }

        await using var verify = new MusicHoarderDbContext(opts);
        var song = Assert.Single(await verify.Songs.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(10, song.Id);
        Assert.Equal(".flac", song.Extension);
        var request = await verify.UpgradeRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Completed, request.Status);
    }

    [Fact]
    public async Task Merge_NotReadyUntilProvisionalHasFingerprint()
    {
        await using var db = CreateDbContext();
        var newFile = WriteStagingFile("better.flac");
        db.Songs.AddRange(
            TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128),
            ProvisionalSong(11, newFile, extension: ".flac", bitrate: 900, fingerprint: null));
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
        await db.SaveChangesAsync();

        var merged = await CreateService(db).SweepAsync(default);

        Assert.Equal(0, merged);
        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.AwaitingIngest, request.Status); // still waiting
        Assert.Equal(2, await db.Songs.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Merge_RejectsFileThatIsNotActuallyBetter()
    {
        await using var db = CreateDbContext();
        // Advertised as FLAC upgrade, but the real scanned file is a 96 kbps opus.
        var newFile = WriteStagingFile("liar.opus");
        db.Songs.AddRange(
            TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128),
            ProvisionalSong(11, newFile, extension: ".opus", bitrate: 96, fingerprint: "FP-new"));
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
        await db.SaveChangesAsync();

        await CreateService(db).SweepAsync(default);

        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Failed, request.Status);
        Assert.Contains("not actually better", request.Error);
        var song = Assert.Single(await db.Songs.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(10, song.Id);                 // target untouched
        Assert.Equal(".opus", song.Extension);
        Assert.False(File.Exists(newFile));        // rejected download cleaned up
    }

    [Fact]
    public async Task Merge_RejectsDurationMismatch()
    {
        await using var db = CreateDbContext();
        var newFile = WriteStagingFile("wrong.flac");
        var provisional = ProvisionalSong(11, newFile, extension: ".flac", bitrate: 900, fingerprint: "FP-new");
        provisional.DurationSeconds = 95; // target is 200s — different recording
        db.Songs.AddRange(
            TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128),
            provisional);
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
        await db.SaveChangesAsync();

        await CreateService(db).SweepAsync(default);

        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Failed, request.Status);
        Assert.Contains("duration mismatch", request.Error);
    }

    [Fact]
    public async Task Merge_FailsRequestWhenDownloadedFileVanished()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128));
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: Path.Combine(stagingDir, "gone.flac").Replace('\\', '/')));
        await db.SaveChangesAsync();

        await CreateService(db).SweepAsync(default);

        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Failed, request.Status);
        Assert.Contains("disappeared", request.Error);
    }

    [Fact]
    public async Task Merge_ClearsStaleDuplicateFlagOnTarget()
    {
        await using var db = CreateDbContext();
        var newFile = WriteStagingFile("better.flac");
        var target = TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128);
        target.MarkAsDuplicate(11); // dedup raced and flagged target as duplicate of the provisional
        db.Songs.AddRange(target, ProvisionalSong(11, newFile, extension: ".flac", bitrate: 900, fingerprint: "FP-new"));
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
        await db.SaveChangesAsync();

        await CreateService(db).SweepAsync(default);

        var song = Assert.Single(await db.Songs.IgnoreQueryFilters().ToListAsync());
        Assert.False(song.IsDuplicate);
        Assert.Null(song.DuplicateOfId);
    }

    // ── Same-recording fingerprint gate ─────────────────────────────────────

    // Real fpcalc fingerprints: FpA128 and FpAFlac are the same recording (mp3 vs flac); FpBFlac differs.
    private const string FpA128 =
        "AQAAU4ulRFHUIH70Qs0zJcHXIXyG7UefZUvxjxiVTuHw8Af1K8aj4JZ0vAo-LkeY0lqQ7BlzfFmOJo3RjjxKZiEafcafQueEH4-oG88U4dlh4Uek6IGuG5aSM0V1F7nw4LsQn1AUJWoaVLrxMwgPJruY4RXGJRKc7PiRz1DFI86JE3sOPpaCOVF05NCP_ME6pQmaKbGigmGOHD_eR8EthNKio0m0NcKe43hcPLMqjEzQ6-Af_AiS4z-e4_iOP0d_HCCIMsgIYgBQQHgiDBMCGEaMQlQoIQhDDENiCEDVGWkcsIJZELAiZBgBhARSUFUZA1QZgBmQggiGGROGCQ4QUcACEQ";
    private const string FpAFlac =
        "AQAAU4ulRFHUIH70Qs0zJcHXIXwmbKfRZ9lS_CNGpVM4PPxB_YrxKLglHa-Cj8sRprQWJHvGHF-Wo0ljtCOPklmIRp_xp9A54ccj6sYzRXh2WPgRKXqg64al5ExR3UUuPPguxCcURYmaBpVu_AzCg8kuZniFcYkEJzt-5DNU8Yhz4sSeg4-lYE4UHTn0I3-wTmmCZkqsqGCYI8eP91FwC6G06GgSbY2w5zgeF8-sCiMT9Dr4Bz-C5PiP5zi-48_RHwcgiDJIEAOAAsITYZgQwDBiFKJCCUEYYhgSQwCqzkjjgBXMgoAVIcMIICSQgqrKGKDKAMyAFEQwzJgwTHCAiAIWiA";
    private const string FpBFlac =
        "AQAAU5ESSYmkRFISHId56DhyUMd3HEfOQ8R35DCOVDbOgsqHGxJO-PjxHY5I4mEWA4-P4yN8_EN6vKLRk0SWwydO6McP48cb6NrxgML_4BJxuPjxXYK-4QcPfTmMUyIs44dzFv8Fo99A9IL_ouPx4wdzg_pxHXJx4jmqQ3tWHHZqAGGdMlQwwywTDkhLgdXKcAeEIR4Z4QwQADjCgDMCGUIFAYZSgYSwgAtjJQQAAEaUUNQZAARF1BkAAAA";

    [Fact]
    public async Task Merge_RejectsDifferentRecording_ByFingerprint()
    {
        await using var db = CreateDbContext();
        var newFile = WriteStagingFile("wrong-song.flac", bytes: 2048);
        // Duration agrees and it's a "better" FLAC — only the fingerprint reveals it's a different track.
        db.Songs.AddRange(
            TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128, fingerprint: FpA128),
            ProvisionalSong(11, newFile, extension: ".flac", bitrate: 900, fingerprint: FpBFlac));
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
        await db.SaveChangesAsync();

        await CreateService(db).SweepAsync(default);

        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Failed, request.Status);
        Assert.Contains("different recording", request.Error);
        var song = Assert.Single(await db.Songs.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(".opus", song.Extension); // target untouched
        Assert.False(File.Exists(newFile));    // rejected download cleaned up
    }

    [Fact]
    public async Task Merge_AcceptsSameRecording_AcrossCodecs()
    {
        await using var db = CreateDbContext();
        var newFile = WriteStagingFile("better.flac", bytes: 2048);
        db.Songs.AddRange(
            TargetSong(10, WriteStagingFile("old.opus"), extension: ".opus", bitrate: 128, fingerprint: FpA128),
            ProvisionalSong(11, newFile, extension: ".flac", bitrate: 900, fingerprint: FpAFlac));
        db.UpgradeRequests.Add(AwaitingIngest(1, songId: 10, downloadedPath: newFile));
        await db.SaveChangesAsync();

        var merged = await CreateService(db).SweepAsync(default);

        Assert.Equal(1, merged);
        var song = Assert.Single(await db.Songs.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(".flac", song.Extension); // upgrade applied
        Assert.Equal(UpgradeRequestStatus.Completed, (await db.UpgradeRequests.SingleAsync()).Status);
    }

    // ── Strictly-better candidate filter ────────────────────────────────────

    [Fact]
    public void FilterStrictlyBetter_EnforcesTierFloorAndScoreImprovement()
    {
        static UpgradeFloor Floor(SongMetadata s) =>
            new(AudioQuality.TierFor(s.Extension), AudioQuality.Score(s), s.DurationMs);
        var opusFloor = Floor(TargetSong(1, "/x/a.opus", extension: ".opus", bitrate: 128));
        var flacFloor = Floor(TargetSong(2, "/x/b.flac", extension: ".flac", bitrate: 900));

        SlskdCandidate Candidate(string ext, int? bitrate) => new(
            "peer", new SlskdFile($@"a\song{ext}", 1000, bitrate, 200, null), true, 0, 100);

        // For a lossy target: higher-bitrate lossy and any lossless qualify; lower lossy doesn't.
        var forOpus = SlskdDownloadProvider.FilterStrictlyBetter(
            [Candidate(".mp3", 320), Candidate(".flac", null), Candidate(".opus", 96)], opusFloor);
        Assert.Equal(2, forOpus.Count);

        // For a lossless target: fat lossy never qualifies; same-tier same-score doesn't either.
        var forFlac = SlskdDownloadProvider.FilterStrictlyBetter(
            [Candidate(".mp3", 320), Candidate(".flac", 900)], flacFloor);
        Assert.Empty(forFlac);

        // Higher-bitrate FLAC over FLAC does qualify.
        var better = SlskdDownloadProvider.FilterStrictlyBetter([Candidate(".flac", 1411)], flacFloor);
        Assert.Single(better);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SongMetadata TargetSong(
        int id, string sourcePath, string extension, int? bitrate, string? fingerprint = "FP-old") => new()
    {
        Id = id,
        OwnerUserId = Owner,
        SourcePath = sourcePath.Replace('\\', '/'),
        FileSizeBytes = 1000,
        FileName = Path.GetFileName(sourcePath),
        Extension = extension,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Fingerprint = fingerprint,
        Bitrate = bitrate,
        DurationSeconds = 200,
        DurationMs = 200_000,
        Artist = "Artist",
        Title = "Song",
        EnrichmentStatus = EnrichmentStatus.Matched,
    };

    private static SongMetadata ProvisionalSong(
        int id, string sourcePath, string extension, int? bitrate, string? fingerprint) => new()
    {
        Id = id,
        OwnerUserId = Owner,
        SourcePath = sourcePath.Replace('\\', '/'),
        FileSizeBytes = 2048,
        FileName = Path.GetFileName(sourcePath),
        Extension = extension,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Fingerprint = fingerprint,
        Bitrate = bitrate,
        DurationSeconds = 200,
        DurationMs = 200_000,
        Artist = "Artist",
        Title = "Song",
    };

    private static UpgradeRequest AwaitingIngest(int id, int songId, string downloadedPath) => new()
    {
        Id = id,
        SongId = songId,
        OwnerUserId = Owner,
        Status = UpgradeRequestStatus.AwaitingIngest,
        DownloadedFilePath = downloadedPath.Replace('\\', '/'),
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private string WriteStagingFile(string name, int bytes = 1024)
    {
        var path = Path.Combine(stagingDir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path.Replace('\\', '/');
    }

    private UpgradeMergeService CreateService(MusicHoarderDbContext db)
    {
        var slskd = new StaticOptionsMonitor<SlskdOptions>(new SlskdOptions
        {
            BaseUrl = "http://slskd:5030",
            ApiKey = "key",
            DownloadsDirectory = "/slskd-staging",
        });
        var enricher = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadDirectory = stagingDir, // staging files count as managed → old source deletable
        });
        return new UpgradeMergeService(
            db, new JobManager(), new OwnerLookupService(), slskd, enricher,
            NullLogger<UpgradeMergeService>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class AnonymousUserAccessor : ICurrentUserAccessor
    {
        public CurrentUser? User => null;
        public Guid UserId => Guid.Empty;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
