using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Persistence;

public class IngestRunScopingTests
{
    [Fact]
    public async Task IngestRuns_query_filter_scopes_to_current_user()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.IngestRuns.AddRange(
                MakeRun(TestUsers.OwnerId, "/owner/src"),
                MakeRun(TestUsers.DemoId, "/demo/src"));
            await seed.SaveChangesAsync();
        }

        // The /runs endpoint runs as the owner and relies on this filter — owner sees only theirs.
        await using (var asOwner = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser)))
        {
            var visible = await asOwner.IngestRuns.Select(r => r.SourcePath).ToListAsync();
            Assert.Equal(["/owner/src"], visible);
        }

        // A demo user must never see the owner's runs (system info is owner-only).
        await using (var asDemo = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.DemoUser)))
        {
            var visible = await asDemo.IngestRuns.Select(r => r.SourcePath).ToListAsync();
            Assert.Equal(["/demo/src"], visible);
        }
    }

    private static IngestRun MakeRun(Guid ownerId, string sourcePath) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        StartedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Status = IngestRunStatus.Completed,
        SourcePath = sourcePath,
        DestinationPath = "/dst",
    };
}
