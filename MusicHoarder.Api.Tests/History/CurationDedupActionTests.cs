using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.History;
using MusicHoarder.Api.History.Sources;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.History;

/// <summary>
/// The History feed names each dedup action by the audit source its rows were written under, so the
/// spelling a curation tool writes and the one the feed reads must stay the same.
/// </summary>
public class CurationDedupActionTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ActivityWindow Window = new(Now.AddDays(-7), Now.AddDays(1), 4000);

    [Theory]
    [InlineData("artist-merge", "artists-merged", "Merged two spellings of an artist")]
    [InlineData("album-merge", "albums-merged", "Merged two versions of an album")]
    [InlineData("artist-credit-split", "credit-split", "Split a combined artist credit")]
    [InlineData("album-identity-heal", "album-healed", "Healed an album split across artist spellings")]
    public async Task Curation_NamesEachDedupActionBySource(string source, string kind, string label)
    {
        using var db = new MusicHoarderDbContext(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var history = new StubDedupHistory(new DedupActionSummary(
            source, Now.AddHours(-1), Now.AddHours(-1).Ticks, 2, 2, [], Reverted: false, Revertible: true));

        var entry = Assert.Single(await new CurationActivitySource(db, history).CollectAsync(Window, default));
        Assert.Equal(kind, entry.Kind);
        Assert.StartsWith(label + " — ", entry.Headline);
    }

    private sealed class StubDedupHistory(params DedupActionSummary[] actions) : IDedupActionHistory
    {
        public Task<IReadOnlyList<DedupActionSummary>> ListAsync(int take = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DedupActionSummary>>(actions);

        public Task<DedupActionRevertResult> RevertAsync(
            Guid ownerUserId, string source, long batchTicks, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
