using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Provider-chain semantics: fall through only on NotFound, stop on success, stop on a transient
/// error, and record which provider actually fulfilled (or last attempted) each item.
/// </summary>
public class WishlistDownloadProcessorChainTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task Chain_FirstProviderNotFound_FallsThroughToSecond()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("t1"));
        await db.SaveChangesAsync();

        var slskd = new NamedFakeProvider("slskd", _ => DownloadResult.Missing("nothing on soulseek"));
        var ytdlp = new NamedFakeProvider("yt-dlp", _ => DownloadResult.Ok("/downloads/x.opus"));
        var processor = CreateProcessor([slskd, ytdlp], ["slskd", "yt-dlp"]);

        var (_, downloaded) = await processor.ProcessBatchAsync(db, Owner, default);

        Assert.Equal(1, downloaded);
        Assert.Equal(1, slskd.Calls);
        Assert.Equal(1, ytdlp.Calls);
        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.Downloaded, item.Status);
        Assert.Equal("yt-dlp", item.DownloadProvider);
    }

    [Fact]
    public async Task Chain_FirstProviderSucceeds_SecondNeverCalled()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("t1"));
        await db.SaveChangesAsync();

        var slskd = new NamedFakeProvider("slskd", _ => DownloadResult.Ok("/downloads/x.flac"));
        var ytdlp = new NamedFakeProvider("yt-dlp", _ => DownloadResult.Ok("/downloads/x.opus"));
        var processor = CreateProcessor([slskd, ytdlp], ["slskd", "yt-dlp"]);

        await processor.ProcessBatchAsync(db, Owner, default);

        Assert.Equal(1, slskd.Calls);
        Assert.Equal(0, ytdlp.Calls);
        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("slskd", item.DownloadProvider);
    }

    [Fact]
    public async Task Chain_FirstProviderTransientError_StopsChainAndFailsItem()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("t1"));
        await db.SaveChangesAsync();

        var slskd = new NamedFakeProvider("slskd", _ => DownloadResult.Failed("slskd unreachable"));
        var ytdlp = new NamedFakeProvider("yt-dlp", _ => DownloadResult.Ok("/downloads/x.opus"));
        var processor = CreateProcessor([slskd, ytdlp], ["slskd", "yt-dlp"]);

        var (_, downloaded) = await processor.ProcessBatchAsync(db, Owner, default);

        Assert.Equal(0, downloaded);
        Assert.Equal(0, ytdlp.Calls); // error must NOT burn the fallback's quota
        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.Failed, item.Status);
        Assert.Equal("slskd", item.DownloadProvider);
    }

    [Fact]
    public async Task Chain_AllProvidersNotFound_ItemGoesNotFound()
    {
        await using var db = CreateDbContext();
        db.WishlistItems.Add(MakePending("t1"));
        await db.SaveChangesAsync();

        var slskd = new NamedFakeProvider("slskd", _ => DownloadResult.Missing("no"));
        var ytdlp = new NamedFakeProvider("yt-dlp", _ => DownloadResult.Missing("also no"));
        var processor = CreateProcessor([slskd, ytdlp], ["slskd", "yt-dlp"]);

        await processor.ProcessBatchAsync(db, Owner, default);

        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(WishlistItemStatus.NotFound, item.Status);
        Assert.Equal("yt-dlp", item.DownloadProvider); // last attempted
    }

    [Fact]
    public void ResolveProviders_EmptyChain_FallsBackToLegacySingleProvider()
    {
        var slskd = new NamedFakeProvider("slskd", _ => DownloadResult.Missing(""));
        var ytdlp = new NamedFakeProvider("yt-dlp", _ => DownloadResult.Missing(""));
        var processor = CreateProcessor([slskd, ytdlp], chain: [], legacyName: "yt-dlp");

        var resolved = processor.ResolveProviders();

        Assert.Single(resolved);
        Assert.Equal("yt-dlp", resolved[0].Name);
    }

    [Fact]
    public void ResolveProviders_UnknownNamesSkipped_DuplicatesCollapsed()
    {
        var ytdlp = new NamedFakeProvider("yt-dlp", _ => DownloadResult.Missing(""));
        var processor = CreateProcessor([ytdlp], chain: ["slskd", "yt-dlp", "yt-dlp"]);

        var resolved = processor.ResolveProviders();

        Assert.Single(resolved);
        Assert.Equal("yt-dlp", resolved[0].Name);
    }

    private static WishlistItem MakePending(string trackId) => new()
    {
        OwnerUserId = Owner,
        SpotifyTrackId = trackId,
        Artist = "Artist",
        Title = "Title",
        Album = "Album",
        DurationMs = 200_000,
        Status = WishlistItemStatus.Pending,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static WishlistDownloadProcessor CreateProcessor(
        IDownloadProvider[] providers, string[] chain, string legacyName = "yt-dlp")
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/src",
            DestinationDirectory = "/dest",
            DownloadDirectory = "/downloads",
            DownloadProvider = legacyName,
            DownloadProviders = chain,
            DownloadConcurrency = 2,
        });
        return new WishlistDownloadProcessor(
            providers,
            new DownloadProgressTracker(),
            options,
            NullLogger<WishlistDownloadProcessor>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class NamedFakeProvider(string name, Func<DownloadRequest, DownloadResult> fn) : IDownloadProvider
    {
        private int _calls;
        public int Calls => _calls;

        public string Name => name;

        public Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(fn(req));
        }
    }
}
