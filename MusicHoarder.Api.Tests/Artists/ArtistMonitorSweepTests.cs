using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Deezer;
using static MusicHoarder.Api.Tests.Artists.ArtistTestKit;

namespace MusicHoarder.Api.Tests.Artists;

public class ArtistMonitorSweepTests
{
    private const string ArtistId = "27";
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FirstCheck_RecordsTheBackCatalogAsTheBaseline_AndQueuesNothing()
    {
        await using var db = NewContext();
        var deezer = Catalog(Release("d", "Discovery", date: "2001-03-12"), Release("h", "Homework", date: "1997-01-20"));
        var follow = await FollowAsync(db, followedAt: Now);
        var (sweep, notifier) = Sweep(db, deezer);

        await sweep.CheckAsync(follow.Id, Now, default);

        var releases = await db.FollowedArtistReleases.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, releases.Count);
        Assert.All(releases, r => Assert.Equal(FollowedReleaseDecision.Baseline, r.Decision));
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(notifier.Sent);
        Assert.NotNull((await db.FollowedArtists.IgnoreQueryFilters().SingleAsync()).NextCheckAtUtc);
    }

    [Fact]
    public async Task ANewRelease_IsQueuedAsArtistMonitorWork_AndAnnounced()
    {
        await using var db = NewContext();
        var deezer = Catalog(Release("d", "Discovery", date: "2001-03-12"));
        var follow = await FollowAsync(db, followedAt: Now.AddDays(-30));
        var (sweep, notifier) = Sweep(db, deezer);
        await sweep.CheckAsync(follow.Id, Now.AddDays(-30), default);

        deezer.ArtistReleases[ArtistId]!.Add(Release("n", "New Album", date: "2026-11-13"));
        deezer.Albums["n"] = Album("n", "New Album", "Daft Punk", "Intro", "Outro");
        var result = await sweep.CheckAsync(follow.Id, Now, default);

        Assert.Equal((1, 2), (result.NewReleases, result.TracksQueued));
        var items = await db.WishlistItems.IgnoreQueryFilters().ToListAsync();
        Assert.All(items, i => Assert.Equal(WishlistItemOrigin.ArtistMonitor, i.Origin));
        var decided = await db.FollowedArtistReleases.IgnoreQueryFilters().SingleAsync(r => r.DeezerAlbumId == "n");
        Assert.Equal((FollowedReleaseDecision.Queued, 2), (decided.Decision, decided.TracksQueued));
        var notice = Assert.Single(notifier.Sent);
        Assert.Equal(("Daft Punk", "New Album", 2), (notice.ArtistName, notice.Title, notice.TracksQueued));

        // Decided once: the next check finds nothing new.
        var again = await sweep.CheckAsync(follow.Id, Now.AddDays(1), default);
        Assert.Equal(0, again.NewReleases);
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task AKindTheFollowDoesNotWatch_IsLeftUndecided_UntilItIsWatched()
    {
        await using var db = NewContext();
        var deezer = Catalog();
        var follow = await FollowAsync(db, followedAt: Now.AddDays(-5));
        var (sweep, _) = Sweep(db, deezer);
        await sweep.CheckAsync(follow.Id, Now.AddDays(-5), default);

        deezer.ArtistReleases[ArtistId]!.Add(Release("s", "Hot Single", "single", "2026-11-10"));
        deezer.Albums["s"] = Album("s", "Hot Single", "Daft Punk", "Hot Single");
        await sweep.CheckAsync(follow.Id, Now, default);
        Assert.Empty(await db.FollowedArtistReleases.IgnoreQueryFilters().ToListAsync());

        var tracked = await db.FollowedArtists.IgnoreQueryFilters().SingleAsync();
        tracked.ReleaseKinds |= ArtistReleaseKinds.Single;
        await db.SaveChangesAsync();
        var result = await sweep.CheckAsync(follow.Id, Now.AddHours(1), default);

        Assert.Equal(1, result.TracksQueued);
    }

    [Fact]
    public async Task ABackFilledOldRelease_IsNotNews()
    {
        await using var db = NewContext();
        var deezer = Catalog();
        var follow = await FollowAsync(db, followedAt: Now.AddDays(-1));
        var (sweep, notifier) = Sweep(db, deezer);
        await sweep.CheckAsync(follow.Id, Now.AddDays(-1), default);

        deezer.ArtistReleases[ArtistId]!.Add(Release("old", "Lost Tapes", date: "1995-06-01"));
        await sweep.CheckAsync(follow.Id, Now, default);

        var row = await db.FollowedArtistReleases.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(FollowedReleaseDecision.SkippedOld, row.Decision);
        Assert.Empty(notifier.Sent);
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task AnAnnouncedRelease_WaitsUntilItIsOut()
    {
        await using var db = NewContext();
        var deezer = Catalog();
        var follow = await FollowAsync(db, followedAt: Now.AddDays(-1));
        var (sweep, _) = Sweep(db, deezer);
        await sweep.CheckAsync(follow.Id, Now.AddDays(-1), default);

        deezer.ArtistReleases[ArtistId]!.Add(Release("f", "Coming Soon", date: "2026-12-01"));
        deezer.Albums["f"] = Album("f", "Coming Soon", "Daft Punk", "Track");
        await sweep.CheckAsync(follow.Id, Now, default);
        Assert.Empty(await db.FollowedArtistReleases.IgnoreQueryFilters().ToListAsync());

        var result = await sweep.CheckAsync(follow.Id, new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc), default);
        Assert.Equal(1, result.TracksQueued);
    }

    [Fact]
    public async Task AReleaseAnnouncedBeforeTheFollow_IsNotBaseline_AndDownloadsWhenOut()
    {
        await using var db = NewContext();
        var deezer = Catalog(Release("old", "Discovery", date: "2001-03-12"), Release("f", "Coming Soon", date: "2026-12-01"));
        deezer.Albums["f"] = Album("f", "Coming Soon", "Daft Punk", "Track");
        var follow = await FollowAsync(db, followedAt: Now);
        var (sweep, _) = Sweep(db, deezer);

        await sweep.CheckAsync(follow.Id, Now, default);
        Assert.Equal(["Discovery"], (await db.FollowedArtistReleases.IgnoreQueryFilters().ToListAsync()).Select(r => r.Title));

        var result = await sweep.CheckAsync(follow.Id, new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc), default);
        Assert.Equal(1, result.TracksQueued);
    }

    [Fact]
    public async Task WithAutoDownloadOff_ANewReleaseIsOnlyNoticed()
    {
        await using var db = NewContext();
        var deezer = Catalog();
        var follow = await FollowAsync(db, followedAt: Now.AddDays(-1), autoDownload: false);
        var (sweep, notifier) = Sweep(db, deezer);
        await sweep.CheckAsync(follow.Id, Now.AddDays(-1), default);

        deezer.ArtistReleases[ArtistId]!.Add(Release("n", "New Album", date: "2026-11-13"));
        await sweep.CheckAsync(follow.Id, Now, default);

        Assert.Equal(FollowedReleaseDecision.Noticed, (await db.FollowedArtistReleases.IgnoreQueryFilters().SingleAsync()).Decision);
        Assert.False(Assert.Single(notifier.Sent).Queued);
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task AnUnreadableListing_DecidesNothing_AndIsNotTheBaseline()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.ArtistReleases[ArtistId] = null;
        var follow = await FollowAsync(db, followedAt: Now);
        var (sweep, _) = Sweep(db, deezer);

        await sweep.CheckAsync(follow.Id, Now, default);

        var tracked = await db.FollowedArtists.IgnoreQueryFilters().SingleAsync();
        Assert.Null(tracked.LastCheckedAtUtc);
        Assert.NotNull(tracked.LastError);
        Assert.Empty(await db.FollowedArtistReleases.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task AFailedRelease_IsRetriedOnTheNextCheck()
    {
        await using var db = NewContext();
        var deezer = Catalog();
        var follow = await FollowAsync(db, followedAt: Now.AddDays(-1));
        var (sweep, _) = Sweep(db, deezer);
        await sweep.CheckAsync(follow.Id, Now.AddDays(-1), default);

        // Listed, but its tracklist cannot be read yet.
        deezer.ArtistReleases[ArtistId]!.Add(Release("n", "New Album", date: "2026-11-13"));
        await sweep.CheckAsync(follow.Id, Now, default);
        Assert.Equal(FollowedReleaseDecision.Failed, (await db.FollowedArtistReleases.IgnoreQueryFilters().SingleAsync()).Decision);

        deezer.Albums["n"] = Album("n", "New Album", "Daft Punk", "Intro");
        await sweep.CheckAsync(follow.Id, Now.AddHours(13), default);
        Assert.Equal(FollowedReleaseDecision.Queued, (await db.FollowedArtistReleases.IgnoreQueryFilters().SingleAsync()).Decision);
    }

    [Fact]
    public async Task Sweep_ChecksOnlyTheFollowsThatAreDue()
    {
        await using var db = NewContext();
        var deezer = Catalog();
        var due = await FollowAsync(db, followedAt: Now);
        var notDue = await FollowAsync(db, followedAt: Now, deezerId: "99");
        notDue.NextCheckAtUtc = DateTime.UtcNow.AddDays(1);
        await db.SaveChangesAsync();
        var (sweep, _) = Sweep(db, deezer);

        var result = await sweep.SweepAsync(default);

        Assert.Equal(1, result.ArtistsChecked);
        Assert.NotNull((await db.FollowedArtists.IgnoreQueryFilters().SingleAsync(f => f.Id == due.Id)).LastCheckedAtUtc);
    }

    [Fact]
    public async Task Sweep_DisabledMonitor_IsIdle()
    {
        await using var db = NewContext();
        await FollowAsync(db, followedAt: Now);
        var (sweep, _) = Sweep(db, Catalog(), o => o.EnableArtistMonitor = false);

        var result = await sweep.SweepAsync(default);

        Assert.Equal(ArtistMonitorResult.IdleDisabled, result.IdleReason);
    }

    private static FakeDeezerCatalogService Catalog(params MusicHoarder.Api.Deezer.DeezerArtistRelease[] releases)
    {
        var deezer = new FakeDeezerCatalogService();
        deezer.ArtistReleases[ArtistId] = releases.ToList();
        return deezer;
    }

    private static async Task<FollowedArtist> FollowAsync(
        MusicHoarderDbContext db, DateTime followedAt, bool autoDownload = true, string deezerId = ArtistId)
    {
        var follow = new FollowedArtist
        {
            OwnerUserId = Owner,
            DeezerArtistId = deezerId,
            Name = "Daft Punk",
            ArtistKey = "daft punk",
            AutoDownload = autoDownload,
            FollowedAtUtc = followedAt,
        };
        db.FollowedArtists.Add(follow);
        await db.SaveChangesAsync();
        return follow;
    }

    private static (ArtistMonitorSweep Sweep, RecordingNotifier Notifier) Sweep(
        MusicHoarderDbContext db, FakeDeezerCatalogService deezer, Action<MusicEnricherOptions>? configure = null)
    {
        var opts = EnricherOptions(configure);
        var notifier = new RecordingNotifier();
        var sweep = new ArtistMonitorSweep(
            db, deezer, Queuer(db, deezer, opts), notifier,
            Microsoft.Extensions.Options.Options.Create(opts), NullLogger<ArtistMonitorSweep>.Instance);
        return (sweep, notifier);
    }
}
