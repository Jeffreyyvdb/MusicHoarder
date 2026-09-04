using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class StagedSourceEndpointsTests
{
    [Fact]
    public void Release_Conflicts_WhileAPurgeIsRunning()
    {
        var jobManager = new JobManager();
        Assert.True(jobManager.TryStartJob(JobType.Purge, out _, out _));
        var tracker = new StagedSourceReleaseTracker();

        var result = EnrichmentEndpoints.StartStagedSourceRelease(
            jobManager, new ThrowingScopeFactory(), tracker, WellKnownUsers.OwnerId, NullLoggerFactory.Instance);

        Assert.Equal(409, GetStatusCode(result));
        Assert.False(tracker.IsRunning);
    }

    [Fact]
    public void Release_Conflicts_WhileAReleaseIsAlreadyRunning()
    {
        var tracker = new StagedSourceReleaseTracker();
        Assert.True(tracker.TryStart("sweep", out _, out _));

        var result = EnrichmentEndpoints.StartStagedSourceRelease(
            new JobManager(), new ThrowingScopeFactory(), tracker, WellKnownUsers.OwnerId, NullLoggerFactory.Instance);

        Assert.Equal(409, GetStatusCode(result));
    }

    [Fact]
    public async Task Release_Accepts_AndRunsToCompletionInTheBackground()
    {
        const string source = "/downloads/track.flac";
        const string destination = "/dest/Artist/2026 - Album/01 - Track.flac";
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [source] = new("audio-bytes-1"),
            [destination] = new("audio-bytes-1"),
        });
        var dbOptions = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using (var seed = new MusicHoarderDbContext(dbOptions))
        {
            var song = new SongMetadata
            {
                OwnerUserId = WellKnownUsers.OwnerId,
                SourcePath = source,
                FileName = "track.flac",
                Extension = ".flac",
                FileSizeBytes = 13,
                LastModifiedUtc = DateTime.UtcNow.AddDays(-1),
                IndexedAtUtc = DateTime.UtcNow.AddDays(-1),
                DurationMs = 180_000,
                EnrichmentStatus = EnrichmentStatus.Matched,
            };
            song.MarkBuildDone(destination);
            song.LibraryBuiltAtUtc = DateTime.UtcNow.AddHours(-1);
            seed.Songs.Add(song);
            await seed.SaveChangesAsync();
        }

        var tracker = new StagedSourceReleaseTracker();
        var services = new ServiceCollection();
        services.AddDbContext<MusicHoarderDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddScoped(_ => new MusicHoarderDbContext(dbOptions));
        services.AddScoped(_ => new StagedSourceReleaseService(
            new MusicHoarderDbContext(dbOptions),
            fs,
            new ConstantProbe(180_000),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
                DownloadDirectory = "/downloads",
                EnableWishlistDownloads = true,
            }),
            new TestOptionsMonitor<SyncOptions>(new SyncOptions()),
            tracker,
            NullLogger<StagedSourceReleaseService>.Instance));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var result = EnrichmentEndpoints.StartStagedSourceRelease(
            new JobManager(), scopeFactory, tracker, WellKnownUsers.OwnerId, NullLoggerFactory.Instance);

        Assert.Equal(202, GetStatusCode(result));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tracker.Get().Status == "running" && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        var snapshot = tracker.Get();
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal("manual", snapshot.Mode);
        Assert.Equal(1, snapshot.Released);
        Assert.False(fs.File.Exists(source));
    }

    private static int? GetStatusCode(Microsoft.AspNetCore.Http.IResult result)
        => (int?)result.GetType().GetProperty("StatusCode")?.GetValue(result);

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("should not be reached");
    }

    private sealed class ConstantProbe(int durationMs) : IAudioFileProbe
    {
        public AudioProbeResult? Probe(string path) => new AudioProbeResult(durationMs);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
