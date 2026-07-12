using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Download;

public class QualityUpgradeServiceTests : IDisposable
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private readonly string stagingDir;

    public QualityUpgradeServiceTests()
    {
        stagingDir = Path.Combine(Path.GetTempPath(), $"mh-qupgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(stagingDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Process_TriesProvidersInOrder_FirstOkWins()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db);
        var downloaded = WriteStagingFile("better.flac");
        var first = new FakeUpgradeProvider("a", DownloadResult.Missing("nope"));
        var second = new FakeUpgradeProvider("b", DownloadResult.Ok(downloaded));

        await CreateService(db, ["a", "b"], first, second).ProcessRequestAsync(1, default);

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.AwaitingIngest, request.Status);
        Assert.Equal(downloaded.Replace('\\', '/'), request.DownloadedFilePath);
        Assert.Contains("\"provider\":\"b\"", request.CandidateInfoJson);
    }

    [Fact]
    public async Task Process_TransientErrorStopsChain()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db);
        var first = new FakeUpgradeProvider("a", DownloadResult.Failed("boom"));
        var second = new FakeUpgradeProvider("b", DownloadResult.Ok(WriteStagingFile("x.flac")));

        await CreateService(db, ["a", "b"], first, second).ProcessRequestAsync(1, default);

        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls); // chain stopped on transient error
        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Failed, request.Status);
        Assert.Contains("boom", request.Error);
    }

    [Fact]
    public async Task Process_AllMissing_MarksNotFound()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db);
        var providers = new[]
        {
            new FakeUpgradeProvider("a", DownloadResult.Missing("nope")),
            new FakeUpgradeProvider("b", DownloadResult.Missing("also nope")),
        };

        await CreateService(db, ["a", "b"], providers).ProcessRequestAsync(1, default);

        Assert.Equal(UpgradeRequestStatus.NotFound, (await db.UpgradeRequests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Process_SkipsProvidersThatCannotBeatTarget()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db);
        var incapable = new FakeUpgradeProvider("a", DownloadResult.Ok(WriteStagingFile("x.flac")))
        {
            CanUpgradeResult = false, // e.g. a lossless-only provider facing an already-lossless target
        };

        await CreateService(db, ["a"], incapable).ProcessRequestAsync(1, default);

        Assert.Equal(0, incapable.Calls);
        Assert.Equal(UpgradeRequestStatus.NotFound, (await db.UpgradeRequests.SingleAsync()).Status);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task SeedSongWithQueuedRequest(MusicHoarderDbContext db)
    {
        db.Songs.Add(new SongMetadata
        {
            Id = 10,
            OwnerUserId = Owner,
            SourcePath = "/src/song.opus",
            FileSizeBytes = 1000,
            FileName = "song.opus",
            Extension = ".opus",
            Bitrate = 128,
            DurationMs = 200_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Artist",
            Title = "Song",
            EnrichmentStatus = EnrichmentStatus.Matched,
        });
        db.UpgradeRequests.Add(new UpgradeRequest
        {
            Id = 1,
            SongId = 10,
            OwnerUserId = Owner,
            Status = UpgradeRequestStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private QualityUpgradeService CreateService(
        MusicHoarderDbContext db, string[] chain, params IUpgradeProvider[] providers)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadDirectory = stagingDir,
            DownloadProviders = chain,
        });
        return new QualityUpgradeService(
            db, providers, new JobManager(), new OwnerLookupService(), options,
            NullLogger<QualityUpgradeService>.Instance);
    }

    private string WriteStagingFile(string name)
    {
        var path = Path.Combine(stagingDir, name);
        File.WriteAllBytes(path, new byte[1024]);
        return path.Replace('\\', '/');
    }

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeUpgradeProvider(string name, DownloadResult result) : IUpgradeProvider
    {
        public string Name => name;
        public bool CanUpgradeResult { get; init; } = true;
        public int Calls { get; private set; }
        public bool CanUpgrade(UpgradeFloor floor) => CanUpgradeResult;
        public Task<DownloadResult> DownloadBetterAsync(DownloadRequest req, UpgradeFloor floor, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
