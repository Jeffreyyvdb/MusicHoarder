using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The memo is never told that anything changed. It is right as long as it only ever answers a
/// stamp with the count that was computed for that same stamp — whatever was asked in between,
/// by whom, and in what order the answers came back.
/// </summary>
public class DetectionCountMemoTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly byte[] Before = [1, 2, 3];
    private static readonly byte[] After = [1, 2, 4];

    [Fact]
    public void TheSameStamp_IsAnsweredFromMemory()
    {
        var memo = new DetectionCountMemo();

        Assert.Equal(6, memo.GetOrCount("artists", Owner, Before, () => 6));
        // A different array with the same bytes: stamps are compared by content.
        Assert.Equal(6, memo.GetOrCount("artists", Owner, [1, 2, 3], () => throw new InvalidOperationException("counted twice")));
    }

    [Fact]
    public void ADifferentStamp_IsCountedAgain_AndReplacesTheOldAnswer()
    {
        var memo = new DetectionCountMemo();
        memo.GetOrCount("artists", Owner, Before, () => 6);

        Assert.Equal(5, memo.GetOrCount("artists", Owner, After, () => 5));
        Assert.Equal(5, memo.GetOrCount("artists", Owner, After, () => throw new InvalidOperationException("counted twice")));
        // The library went back to how it was: that is one more detection, not a stale 6.
        Assert.Equal(7, memo.GetOrCount("artists", Owner, Before, () => 7));
    }

    [Fact]
    public void DetectorsAndOwners_AreKeptApart_EvenUnderOneStamp()
    {
        var memo = new DetectionCountMemo();
        memo.GetOrCount("artists", Owner, Before, () => 6);

        Assert.Equal(2, memo.GetOrCount("albums", Owner, Before, () => 2));
        Assert.Equal(0, memo.GetOrCount("artists", Guid.NewGuid(), Before, () => 0));
        Assert.Equal(6, memo.GetOrCount("artists", Owner, Before, () => throw new InvalidOperationException("counted twice")));
    }

    // Two requests overlap across a rename: the one that read the library first finishes last and
    // stores its now-outdated count. It stores it under its own stamp, so the next request — whose
    // fresh read stamps as the renamed library — is not given it.
    [Fact]
    public void ASlowOlderRequest_CannotPlantItsAnswerUnderANewerStamp()
    {
        var memo = new DetectionCountMemo();

        var older = memo.GetOrCount("artists", Owner, Before, () =>
        {
            memo.GetOrCount("artists", Owner, After, () => 5);
            return 6;
        });

        Assert.Equal(6, older);
        Assert.Equal(5, memo.GetOrCount("artists", Owner, After, () => 5));
        Assert.Equal(6, memo.GetOrCount("artists", Owner, Before, () => 6));
    }

    [Fact]
    public void ItForgetsRatherThanGrowWithoutBound()
    {
        var memo = new DetectionCountMemo();
        memo.GetOrCount("artists", Owner, Before, () => 6);
        for (var i = 0; i < DetectionCountMemo.MaxEntries; i++)
            memo.GetOrCount("artists", Guid.NewGuid(), Before, () => i);

        // Forgotten, so counted again — never answered wrongly.
        Assert.Equal(8, memo.GetOrCount("artists", Owner, Before, () => 8));
    }

    [Fact]
    public void ManyRequestsAtOnce_EachGetTheCountOfTheirOwnStamp()
    {
        var memo = new DetectionCountMemo();
        var owners = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

        Parallel.For(0, 20_000, i =>
        {
            // The "library" a request sees flips between a few states; the count is a function of it.
            var state = (byte)(i % 5);
            var counted = memo.GetOrCount("artists", owners[i % owners.Length], [state], () => state * 10);
            Assert.Equal(state * 10, counted);
        });
    }
}
