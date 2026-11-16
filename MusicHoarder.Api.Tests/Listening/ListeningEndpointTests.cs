using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Listening;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Listening;

public class ListeningEndpointTests
{
    [Fact]
    public async Task Stats_hold_only_the_callers_own_plays()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var at = DateTime.UtcNow.AddHours(-2);
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.ListeningEvents.AddRange(
                Event(TestUsers.OwnerId, 1, "Mine", at),
                Event(TestUsers.FriendId, 2, "Theirs", at),
                Event(TestUsers.FriendId, 3, "Theirs too", at.AddMinutes(1)));
            await seed.SaveChangesAsync();
        }

        var member = new CurrentUser(TestUsers.FriendId, "m@test.local", UserRole.Member, "Member");
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(member));
        var result = await ListeningEndpoints.GetStats(db, CancellationToken.None, "4w", 0);

        var stats = Assert.IsType<Ok<ListeningStats>>(result).Value!;
        Assert.Equal(2, stats.Totals.Streams);
        Assert.DoesNotContain(stats.Recent, r => r.Title == "Mine");
    }

    [Fact]
    public async Task Recent_ignores_the_range_and_an_unknown_range_is_a_bad_request()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.ListeningEvents.Add(Event(TestUsers.OwnerId, 1, "Old", DateTime.UtcNow.AddYears(-3)));
            await seed.SaveChangesAsync();
        }

        var admin = new CurrentUser(TestUsers.OwnerId, "a@test.local", UserRole.Admin, "Admin");
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(admin));

        var stats = Assert.IsType<Ok<ListeningStats>>(
            await ListeningEndpoints.GetStats(db, CancellationToken.None, "4w", 0)).Value!;
        Assert.Equal(0, stats.Totals.Streams);
        Assert.Single(stats.Recent);
        Assert.NotNull(stats.TrackingSinceUtc);

        var bad = await ListeningEndpoints.GetStats(db, CancellationToken.None, "forever", 0);
        Assert.Equal(StatusCodes.Status400BadRequest, (bad as IStatusCodeHttpResult)?.StatusCode);
    }

    private static ListeningEvent Event(Guid user, int songId, string title, DateTime at) => new()
    {
        UserId = user, SongId = songId, Title = title, PlayedAtUtc = at, Artist = "A", DurationSeconds = 100,
    };
}
