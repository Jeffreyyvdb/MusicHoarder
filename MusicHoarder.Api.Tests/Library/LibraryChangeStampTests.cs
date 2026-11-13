using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The parts of the change stamp that do not need Postgres: when it declines to give one, and that
/// every input it folds in moves the value. The row versions themselves come from a statement only
/// Postgres can run, so that half is measured by the performance suite's "unchanged" rows.
/// </summary>
public class LibraryChangeStampTests
{
    private static readonly Guid Caller = TestUsers.FriendId;
    private static readonly Guid GrantorA = TestUsers.OwnerId;
    private static readonly Guid GrantorB = TestUsers.SecondFriendId;

    [Fact]
    public async Task There_is_no_stamp_where_rows_carry_no_version()
    {
        // The in-memory provider has no row versions, so every endpoint test keeps getting the plain
        // 200 it always got.
        await using var db = NewContext(TestCurrentUserAccessor.OwnerUser);

        Assert.Null(await Stamp(db, TestCurrentUserAccessor.OwnerUser).ComputeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task There_is_no_stamp_without_a_caller()
    {
        await using var db = NewContext(caller: null);

        Assert.Null(await Stamp(db, caller: null).ComputeAsync(CancellationToken.None));
    }

    [Fact]
    public void The_same_inputs_give_the_same_stamp()
    {
        Assert.Equal(Compose(), Compose());
    }

    [Fact]
    public void Every_input_moves_the_stamp()
    {
        var baseline = Compose();

        Assert.NotEqual(baseline, Compose(processId: "another-process"));
        Assert.NotEqual(baseline, Compose(callerId: TestUsers.DemoId));
        Assert.NotEqual(baseline, Compose(grantors: []));
        Assert.NotEqual(baseline, Compose(grantors: [GrantorA, GrantorB]));
        Assert.NotEqual(baseline, Compose(downloadDirectory: "/staging"));
        Assert.NotEqual(baseline, Compose(syncedSourceDirectory: "/synced"));
        Assert.NotEqual(baseline, Compose(rowVersions: "10001:42|0:0"));
    }

    [Fact]
    public void Grantor_order_is_part_of_the_stamp()
    {
        // The lists append shared songs grantor by grantor, so the same two grantors the other way
        // round is a different response.
        Assert.NotEqual(Compose(grantors: [GrantorA, GrantorB]), Compose(grantors: [GrantorB, GrantorA]));
    }

    [Fact]
    public void Neighbouring_parts_cannot_run_together()
    {
        Assert.NotEqual(
            Compose(downloadDirectory: "/a", syncedSourceDirectory: "/b/c"),
            Compose(downloadDirectory: "/a/b", syncedSourceDirectory: "/c"));
        Assert.NotEqual(
            Compose(downloadDirectory: "", syncedSourceDirectory: "/x"),
            Compose(downloadDirectory: "/x", syncedSourceDirectory: ""));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static string Compose(
        string processId = "process",
        Guid? callerId = null,
        Guid[]? grantors = null,
        string? downloadDirectory = "/downloads",
        string? syncedSourceDirectory = "",
        string rowVersions = "10000:42|0:0") =>
        LibraryChangeStamp.Compose(
            processId, callerId ?? Caller, grantors ?? [GrantorA], downloadDirectory, syncedSourceDirectory, rowVersions);

    private static MusicHoarderDbContext NewContext(CurrentUser? caller) =>
        new(
            new DbContextOptionsBuilder<MusicHoarderDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            new TestCurrentUserAccessor(caller));

    private static LibraryChangeStamp Stamp(MusicHoarderDbContext db, CurrentUser? caller) =>
        new(
            db,
            new TestCurrentUserAccessor(caller),
            TestLibraryScope.For(caller),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
            }),
            Microsoft.Extensions.Options.Options.Create(new SyncOptions()),
            NullLogger<LibraryChangeStamp>.Instance);
}
