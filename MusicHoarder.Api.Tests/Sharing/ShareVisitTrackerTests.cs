using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Sharing;

public class ShareVisitTrackerTests
{
    private const string Browser =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    private static readonly DateTime Noon = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Records_a_view_with_its_source_and_no_address()
    {
        await using var db = NewDb();
        var share = await SeedShareAsync(db);
        var tracker = new ShareVisitTracker(new StepClock(Noon), perShareMinuteCap: 100);

        var recorded = await tracker.TryRecordAsync(
            db, share, ShareVisitKind.View, songId: null,
            Visitor(ip: "203.0.113.7", referrer: "https://www.tiktok.com/"), CancellationToken.None);

        Assert.True(recorded);
        var visit = Assert.Single(await db.ShareVisits.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(share.Id, visit.ShareId);
        Assert.Equal(TestUsers.OwnerId, visit.OwnerUserId);
        Assert.Equal(ShareVisitKind.View, visit.Kind);
        Assert.Null(visit.SongId);
        Assert.Equal("TikTok", visit.Source);
        Assert.Equal(Noon, visit.OccurredAtUtc);
        // The key is a hash, not the address.
        Assert.DoesNotContain("203.0.113.7", visit.VisitorKey);
        Assert.Equal(24, visit.VisitorKey.Length);
    }

    [Fact]
    public async Task Does_not_count_the_links_own_owner()
    {
        await using var db = NewDb();
        var share = await SeedShareAsync(db);
        var tracker = new ShareVisitTracker(new StepClock(Noon), perShareMinuteCap: 100);

        var recorded = await tracker.TryRecordAsync(
            db, share, ShareVisitKind.View, null, Visitor(userId: TestUsers.OwnerId), CancellationToken.None);

        Assert.False(recorded);
        Assert.Empty(await db.ShareVisits.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Counts_a_signed_in_visitor_who_is_not_the_owner()
    {
        await using var db = NewDb();
        var share = await SeedShareAsync(db);
        var tracker = new ShareVisitTracker(new StepClock(Noon), perShareMinuteCap: 100);

        Assert.True(await tracker.TryRecordAsync(
            db, share, ShareVisitKind.View, null, Visitor(userId: TestUsers.FriendId), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)")]
    [InlineData("TelegramBot (like TwitterBot)")]
    [InlineData("Mozilla/5.0 (Linux; Android 5.0) AppleWebKit/537.36 (KHTML, like Gecko) Mobile Safari/537.36 (compatible; Bytespider; spider-feedback@bytedance.com)")]
    [InlineData("curl/8.4.0")]
    public async Task Does_not_count_bots(string? userAgent)
    {
        await using var db = NewDb();
        var share = await SeedShareAsync(db);
        var tracker = new ShareVisitTracker(new StepClock(Noon), perShareMinuteCap: 100);

        var recorded = await tracker.TryRecordAsync(
            db, share, ShareVisitKind.View, null, Visitor(userAgent: userAgent), CancellationToken.None);

        Assert.False(recorded);
    }

    [Fact]
    public async Task A_reload_within_the_window_counts_once_and_after_it_counts_again()
    {
        await using var db = NewDb();
        var share = await SeedShareAsync(db);
        var clock = new StepClock(Noon);
        var tracker = new ShareVisitTracker(clock, perShareMinuteCap: 100);
        var visitor = Visitor(ip: "198.51.100.1");

        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, visitor, CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, visitor, CancellationToken.None));

        // Someone else on the same share is their own visitor.
        Assert.True(await tracker.TryRecordAsync(
            db, share, ShareVisitKind.View, null, Visitor(ip: "198.51.100.2"), CancellationToken.None));

        clock.Advance(ShareVisitTracker.DedupeWindow);
        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, visitor, CancellationToken.None));

        var keys = await db.ShareVisits.IgnoreQueryFilters().Select(v => v.VisitorKey).ToListAsync();
        Assert.Equal(3, keys.Count);
        Assert.Equal(2, keys.Distinct().Count());
    }

    [Fact]
    public async Task Plays_are_de_duplicated_per_track()
    {
        await using var db = NewDb();
        var share = await SeedShareAsync(db);
        var tracker = new ShareVisitTracker(new StepClock(Noon), perShareMinuteCap: 100);
        var visitor = Visitor();

        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, visitor, CancellationToken.None));
        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.Play, 1, visitor, CancellationToken.None));
        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.Play, 2, visitor, CancellationToken.None));
        Assert.False(await tracker.TryRecordAsync(db, share, ShareVisitKind.Play, 1, visitor, CancellationToken.None));

        var plays = await db.ShareVisits.IgnoreQueryFilters().Where(v => v.Kind == ShareVisitKind.Play).ToListAsync();
        Assert.Equal([1, 2], plays.Select(p => p.SongId!.Value).Order());
    }

    [Fact]
    public void The_visitor_key_changes_with_the_day_and_the_share()
    {
        var clock = new StepClock(Noon);
        var tracker = new ShareVisitTracker(clock, perShareMinuteCap: 100);
        var visitor = Visitor();

        var today = tracker.VisitorKeyFor(1, visitor, Noon);
        Assert.Equal(today, tracker.VisitorKeyFor(1, visitor, Noon.AddHours(11)));
        Assert.NotEqual(today, tracker.VisitorKeyFor(2, visitor, Noon));
        Assert.NotEqual(today, tracker.VisitorKeyFor(1, visitor, Noon.AddDays(1)));
    }

    [Fact]
    public async Task Drops_visits_over_the_per_share_cap()
    {
        await using var db = NewDb();
        var share = await SeedShareAsync(db);
        var tracker = new ShareVisitTracker(new StepClock(Noon), perShareMinuteCap: 2);

        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, Visitor(ip: "10.0.0.1"), CancellationToken.None));
        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, Visitor(ip: "10.0.0.2"), CancellationToken.None));
        Assert.False(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, Visitor(ip: "10.0.0.3"), CancellationToken.None));
    }

    [Fact]
    public async Task A_signed_in_visitors_own_filter_does_not_hide_their_earlier_visit()
    {
        // The repeat check runs unfiltered: under a member's tenancy filter the owner's ShareVisit
        // rows are invisible, and every reload would count.
        var options = NewOptions();
        SongShare share;
        await using (var seed = new MusicHoarderDbContext(options))
            share = await SeedShareAsync(seed);

        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.FriendUser));
        var tracker = new ShareVisitTracker(new StepClock(Noon), perShareMinuteCap: 100);
        var visitor = Visitor(userId: TestUsers.FriendId);

        Assert.True(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, visitor, CancellationToken.None));
        Assert.False(await tracker.TryRecordAsync(db, share, ShareVisitKind.View, null, visitor, CancellationToken.None));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static ShareVisitor Visitor(
        Guid? userId = null,
        string? ip = "192.0.2.10",
        string? userAgent = Browser,
        string? referrer = null) => new(userId, ip, userAgent, referrer);

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    /// <summary>An anonymous context — what the beacon endpoints run under.</summary>
    private static MusicHoarderDbContext NewDb() => new(NewOptions(), new TestCurrentUserAccessor(user: null));

    private static async Task<SongShare> SeedShareAsync(MusicHoarderDbContext db)
    {
        var share = new SongShare
        {
            Id = 1,
            OwnerUserId = TestUsers.OwnerId,
            SongId = 1,
            Token = "tok",
            Scope = ShareScope.Album,
            CreatedAtUtc = Noon.AddDays(-1),
        };
        db.SongShares.Add(share);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return share;
    }

    private sealed class StepClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
