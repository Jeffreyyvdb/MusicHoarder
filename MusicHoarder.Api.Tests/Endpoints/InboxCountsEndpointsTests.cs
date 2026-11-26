using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// <c>GET /api/inbox/name-counts</c> and <c>GET /api/inbox/decision-counts</c> as the web shell
/// meets them: two numbers each under the names it reads, for the account that asked, with the
/// expected role gates.
/// </summary>
public class InboxCountsEndpointsTests
{
    [Fact]
    public async Task An_admin_gets_both_counts_for_their_own_account()
    {
        var counter = new FakeCounter(new InboxNameCounts(Artists: 6, Albums: 825));

        var (status, body) = await GetNameCounts(TestCurrentUserAccessor.OwnerUser, counter);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("""{"artists":6,"albums":825}""", body);
        Assert.Equal([TestUsers.OwnerId], counter.AskedFor);
    }

    [Fact]
    public async Task A_member_the_demo_account_and_a_stranger_get_nothing_counted()
    {
        var counter = new FakeCounter(new InboxNameCounts(6, 825));

        Assert.Equal(StatusCodes.Status403Forbidden, (await GetNameCounts(TestCurrentUserAccessor.FriendUser, counter)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await GetNameCounts(TestCurrentUserAccessor.DemoUser, counter)).Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await GetNameCounts(null, counter)).Status);
        Assert.Empty(counter.AskedFor);
    }

    [Fact]
    public async Task DecisionCounts_counts_active_live_duplicate_clusters_and_latest_ai_flagged_grades()
    {
        var dbName = Guid.NewGuid().ToString("N");
        await using var db = NewDb(dbName);

        var s1 = AddSong(db, 1);
        var s2 = AddSong(db, 2);
        var s3 = AddSong(db, 3);
        var s4 = AddSong(db, 4);
        var s5 = AddSong(db, 5);
        var deleted = AddSong(db, 6, deleted: true);
        var s7 = AddSong(db, 7);

        // Cluster 1: {s1, s2, s3} via two active links
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = TestUsers.OwnerId, SongIdLow = s1.Id, SongIdHigh = s2.Id,
            Status = DuplicateLinkStatus.Active, Reasons = DuplicateMatchReason.Metadata, DetectedAtUtc = DateTime.UtcNow,
        });
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = TestUsers.OwnerId, SongIdLow = s2.Id, SongIdHigh = s3.Id,
            Status = DuplicateLinkStatus.Active, Reasons = DuplicateMatchReason.Metadata, DetectedAtUtc = DateTime.UtcNow,
        });
        // Dismissed link: must not count
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = TestUsers.OwnerId, SongIdLow = s4.Id, SongIdHigh = s5.Id,
            Status = DuplicateLinkStatus.Dismissed, Reasons = DuplicateMatchReason.Metadata, DetectedAtUtc = DateTime.UtcNow,
        });
        // Active link to a soft-deleted song: leaves only 1 live member, must not form a cluster
        db.SongDuplicateLinks.Add(new SongDuplicateLink
        {
            OwnerUserId = TestUsers.OwnerId, SongIdLow = s4.Id, SongIdHigh = deleted.Id,
            Status = DuplicateLinkStatus.Active, Reasons = DuplicateMatchReason.Metadata, DetectedAtUtc = DateTime.UtcNow,
        });

        // AI grades: s1 Wrong (counts), s2 Questionable (counts), s3 superseded Wrong -> Good (does not count),
        // deleted Wrong (does not count), s7 Good (does not count).
        db.SongQualityGrades.AddRange(
            Grade(s1.Id, SongQualityVerdict.Wrong, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Grade(s2.Id, SongQualityVerdict.Questionable, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Grade(s3.Id, SongQualityVerdict.Wrong, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Grade(s3.Id, SongQualityVerdict.Good, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            Grade(deleted.Id, SongQualityVerdict.Wrong, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Grade(s7.Id, SongQualityVerdict.Good, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var (status, body) = await GetDecisionCounts(TestCurrentUserAccessor.OwnerUser, dbName);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("""{"dupes":1,"ai":2}""", body);
    }

    [Fact]
    public async Task DecisionCounts_allows_demo_and_rejects_member_and_anonymous()
    {
        var dbName = Guid.NewGuid().ToString("N");
        Assert.Equal(StatusCodes.Status200OK, (await GetDecisionCounts(TestCurrentUserAccessor.DemoUser, dbName)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, (await GetDecisionCounts(TestCurrentUserAccessor.FriendUser, dbName)).Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await GetDecisionCounts(null, dbName)).Status);
    }

    private static MusicHoarderDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(name).Options);

    private static SongMetadata AddSong(MusicHoarderDbContext db, int n, bool deleted = false)
    {
        var song = new SongMetadata
        {
            OwnerUserId = TestUsers.OwnerId,
            SourcePath = $"/music/song{n}.mp3",
            FileName = $"song{n}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            DeletedAtUtc = deleted ? DateTime.UtcNow : null,
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    private static SongQualityGrade Grade(int songId, SongQualityVerdict verdict, DateTime at) => new()
    {
        SongId = songId,
        OwnerUserId = TestUsers.OwnerId,
        Verdict = verdict,
        Score = verdict == SongQualityVerdict.Good ? 85 : 30,
        GradedAtUtc = at,
    };

    // Through the mapped route rather than the handler, so the admin gate is part of what is tested.
    private static async Task<(int Status, string Body)> GetNameCounts(CurrentUser? caller, IInboxNameCounter counter)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(counter);
        builder.Services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(caller));
        builder.Services.AddDbContext<MusicHoarderDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        var app = builder.Build();
        app.MapInboxCountsEndpoints();
        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText == "/api/inbox/name-counts");
        Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);

        using var scope = app.Services.CreateScope();
        var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = body;
        http.SetEndpoint(endpoint);
        await endpoint.RequestDelegate!(http);
        return (http.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static async Task<(int Status, string Body)> GetDecisionCounts(CurrentUser? caller, string dbName)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IInboxNameCounter>(new FakeCounter(new InboxNameCounts(0, 0)));
        builder.Services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(caller));
        builder.Services.AddDbContext<MusicHoarderDbContext>(o => o.UseInMemoryDatabase(dbName));
        var app = builder.Build();
        app.MapInboxCountsEndpoints();
        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText == "/api/inbox/decision-counts");
        Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);

        using var scope = app.Services.CreateScope();
        var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = body;
        http.SetEndpoint(endpoint);
        await endpoint.RequestDelegate!(http);
        return (http.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class FakeCounter(InboxNameCounts counts) : IInboxNameCounter
    {
        public List<Guid> AskedFor { get; } = [];

        public Task<InboxNameCounts> CountAsync(Guid ownerUserId, CancellationToken ct = default)
        {
            AskedFor.Add(ownerUserId);
            return Task.FromResult(counts);
        }
    }
}
