using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Sharing;

public class ShareVisitPerAddressLimitTests
{
    private const string Browser =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    [Fact]
    public async Task One_client_address_cannot_use_up_a_links_visit_budget_by_changing_its_user_agent()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(user: null));
        var share = new SongShare
        {
            Id = 1,
            OwnerUserId = TestUsers.OwnerId,
            SongId = 1,
            Token = "tok",
            Scope = ShareScope.Album,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
        };
        db.SongShares.Add(share);
        await db.SaveChangesAsync();
        using var tracker = new ShareVisitTracker();

        for (var i = 0; i < 150; i++)
        {
            await tracker.TryRecordAsync(
                db, share, ShareVisitKind.View, songId: null,
                new ShareVisitor(null, "192.0.2.10", $"{Browser} v{i}", null), CancellationToken.None);
        }

        var rows = await db.ShareVisits.IgnoreQueryFilters().CountAsync();
        Assert.True(rows < ShareVisitTracker.PerShareMinuteCap / 2, $"recorded {rows} rows from one address");

        // An ordinary visitor from another address is still counted.
        Assert.True(await tracker.TryRecordAsync(
            db, share, ShareVisitKind.View, null,
            new ShareVisitor(null, "198.51.100.77", Browser, null), CancellationToken.None));
    }
}
