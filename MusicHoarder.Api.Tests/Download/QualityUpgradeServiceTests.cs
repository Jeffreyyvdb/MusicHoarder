using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    public async Task Process_UnavailableFallsThrough_ThenOkWins()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db);
        var downloaded = WriteStagingFile("better.flac");
        var first = new FakeUpgradeProvider("spotiflac", DownloadResult.ProviderUnavailable("dns"));
        var second = new FakeUpgradeProvider("slskd", DownloadResult.Ok(downloaded));

        await CreateService(db, ["spotiflac", "slskd"], first, second).ProcessRequestAsync(1, default);

        Assert.Equal(1, second.Calls); // a down provider is skipped, not a chain-stopping error
        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.AwaitingIngest, request.Status);
    }

    [Fact]
    public async Task Process_UnavailableThenMissing_MarksDeferred()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db);
        var first = new FakeUpgradeProvider("spotiflac", DownloadResult.ProviderUnavailable("dns"));
        var second = new FakeUpgradeProvider("slskd", DownloadResult.Missing("nothing better"));

        await CreateService(db, ["spotiflac", "slskd"], first, second).ProcessRequestAsync(1, default);

        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.Deferred, request.Status); // short cooldown, not the 30-day one
        Assert.StartsWith("spotiflac unavailable", request.Error);
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

    [Fact]
    public async Task Process_SkipsDownloadOnlyProvidersWithoutWarning_StillWarnsOnUnknown()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db);
        var downloaded = WriteStagingFile("better.flac");
        var spotiflac = new FakeUpgradeProvider("spotiflac", DownloadResult.Ok(downloaded));
        var ytdlp = new FakeDownloadOnlyProvider("yt-dlp");
        var logger = new RecordingLogger<QualityUpgradeService>();

        var service = CreateService(
            db, ["spotiflac", "yt-dlp", "bogus"], [spotiflac], [ytdlp], logger);
        await service.ProcessRequestAsync(1, default);

        Assert.Equal(UpgradeRequestStatus.AwaitingIngest, (await db.UpgradeRequests.SingleAsync()).Status);
        var warnings = logger.Messages.Where(m => m.Level == LogLevel.Warning).Select(m => m.Message).ToList();
        Assert.DoesNotContain(warnings, w => w.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("bogus", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("   ", "   ", false)]
    [InlineData("sp-stale", null, true)]
    public async Task Process_SkipsStreamingOnlyProviderWithoutCalling_FallsThroughToNonStreamingProvider(
        string? spotifyId, string? isrc, bool isUnreleased)
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db, spotifyId: spotifyId, isrc: isrc, isUnreleased: isUnreleased);
        var downloaded = WriteStagingFile("soulseek.flac");
        var spotiflac = new FakeUpgradeProvider("spotiflac", DownloadResult.Ok(WriteStagingFile("wrong.flac")))
        {
            RequiresStreamingIdentity = true,
        };
        var slskd = new FakeUpgradeProvider("slskd", DownloadResult.Ok(downloaded));

        await CreateService(db, ["spotiflac", "slskd"], spotiflac, slskd).ProcessRequestAsync(1, default);

        Assert.Equal(0, spotiflac.Calls);
        Assert.Equal(1, slskd.Calls);
        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.AwaitingIngest, request.Status);
        Assert.Contains("\"provider\":\"slskd\"", request.CandidateInfoJson);
    }

    [Fact]
    public async Task Process_StreamingOnlyProviderAloneOnIdentitylessSong_MarksNotFoundWithoutCalling()
    {
        await using var db = CreateDbContext();
        await SeedSongWithQueuedRequest(db, spotifyId: null, isrc: null, isUnreleased: false);
        var spotiflac = new FakeUpgradeProvider("spotiflac", DownloadResult.Ok(WriteStagingFile("x.flac")))
        {
            RequiresStreamingIdentity = true,
        };

        await CreateService(db, ["spotiflac"], spotiflac).ProcessRequestAsync(1, default);

        Assert.Equal(0, spotiflac.Calls);
        var request = await db.UpgradeRequests.SingleAsync();
        Assert.Equal(UpgradeRequestStatus.NotFound, request.Status);
        Assert.Contains("streaming identity", request.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task SeedSongWithQueuedRequest(
        MusicHoarderDbContext db,
        string? spotifyId = null,
        string? isrc = null,
        bool isUnreleased = false)
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
            SpotifyId = spotifyId,
            Isrc = isrc,
            IsUnreleased = isUnreleased,
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
        MusicHoarderDbContext db, string[] chain, params IUpgradeProvider[] providers) =>
        CreateService(db, chain, providers, [], NullLogger<QualityUpgradeService>.Instance);

    private QualityUpgradeService CreateService(
        MusicHoarderDbContext db,
        string[] chain,
        IUpgradeProvider[] providers,
        IDownloadProvider[] downloadProviders,
        ILogger<QualityUpgradeService> logger)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadDirectory = stagingDir,
            DownloadProviders = chain,
        });
        return new QualityUpgradeService(
            db, providers, downloadProviders, new JobManager(), new OwnerLookupService(), options, logger);
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
        public bool RequiresStreamingIdentity { get; init; }
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
