using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Tests.Metadata;

/// <summary>
/// A clean edit's label is removed from a title and recognised on a search hit, while titles that merely
/// contain the word, and radio edits, are left alone.
/// </summary>
public class CleanEditLabelTests
{
    [Theory]
    [InlineData("Make Her Say (Clean)", "Make Her Say")]
    [InlineData("Make Her Say [Clean]", "Make Her Say")]
    [InlineData("Make Her Say (Clean Version)", "Make Her Say")]
    [InlineData("Make Her Say (Clean Edit)", "Make Her Say")]
    [InlineData("Make Her Say (Edited)", "Make Her Say")]
    [InlineData("Make Her Say [Edited Version]", "Make Her Say")]
    [InlineData("Make Her Say (Censored)", "Make Her Say")]
    [InlineData("Make Her Say (Censored Version)", "Make Her Say")]
    [InlineData("Make Her Say (Original Clean)", "Make Her Say")]
    [InlineData("Make Her Say (Clean Album Version)", "Make Her Say")]
    [InlineData("Make Her Say (Clean album version)", "Make Her Say")]
    [InlineData("Make Her Say (  CLEAN  )", "Make Her Say")]
    [InlineData("Make Her Say - Clean", "Make Her Say")]
    [InlineData("Make Her Say - Edited Version", "Make Her Say")]
    [InlineData("Make Her Say - Album Version Edited", "Make Her Say")]
    [InlineData("Heart Of A Lion (Kid Cudi Theme Music) (Album Version Edited)", "Heart Of A Lion (Kid Cudi Theme Music)")]
    public void Strip_RemovesTheLabel(string title, string expected) =>
        Assert.Equal(expected, CleanEditLabel.Strip(title));

    [Theory]
    [InlineData("Clean (Taylor's Version)")]
    [InlineData("Burn Clean")]
    [InlineData("So Fresh, So Clean (Radio Mix)")]
    [InlineData("See You in My Nightmares (Bumptempo Squeak E. Clean Remix)")]
    [InlineData("2s N 3s (feat. Clean Bandit)")]
    [InlineData("Clean Up Your Own Yard")]
    [InlineData("Impossible - Radio Edit")]
    [InlineData("Make Her Say")]
    public void Strip_LeavesIdentityWordsAlone(string title) =>
        Assert.Equal(title, CleanEditLabel.Strip(title));

    [Fact]
    public void IsUnrequestedCleanEdit_RejectsOnlyTheCleanHit_OfTheMakeHerSaySearch()
    {
        string[] results =
        [
            "Kid Cudi - Make Her Say (Clean Version) ft. Kanye West, Common",
            "Kid Cudi - Make Her Say (Dirty Official Video)",
            "Kid CuDi Make Her Say",
            "Make Her Say",
        ];

        var flags = results
            .Select(t => CleanEditLabel.IsUnrequestedCleanEdit(t, "Kid Cudi Make Her Say"))
            .ToArray();

        Assert.Equal([true, false, false, false], flags);
    }

    [Theory]
    [InlineData("Make Her Say CLEAN", "Kid Cudi Make Her Say", true)]
    [InlineData("Kid Cudi - Make Her Say (Censored)", "Kid Cudi Make Her Say", true)]
    [InlineData("Heart Of A Lion (Edited)", "Kid Cudi Heart Of A Lion", true)]
    [InlineData("OutKast - So Fresh, So Clean", "OutKast So Fresh, So Clean", false)]
    [InlineData("Clean Bandit - Rather Be", "Clean Bandit Rather Be", false)]
    [InlineData("Some Artist - Song (feat. Clean Bandit)", "Some Artist Song (feat. Clean Bandit)", false)]
    // A request that itself carries the label has it stripped, so a clean hit is still unrequested:
    // the original is always preferred.
    [InlineData("Kid Cudi - Make Her Say (Clean)", "Kid Cudi Make Her Say (Clean)", true)]
    public void IsUnrequestedCleanEdit_ComparesAgainstTheRequest(string candidate, string requested, bool expected) =>
        Assert.Equal(expected, CleanEditLabel.IsUnrequestedCleanEdit(candidate, requested));
}
