using MusicHoarder.Api.Version;

namespace MusicHoarder.Api.Tests.Version;

public class SemVerComparerTests
{
    [Theory]
    [InlineData("1.4.2", "1.5.0")] // patch/minor bump
    [InlineData("1.4.2", "2.0.0")] // major bump
    [InlineData("1.4.2", "1.4.3")] // patch bump
    [InlineData("1.4", "1.4.1")]   // two-part current, three-part latest
    [InlineData("1.4.2", "v1.5.0")] // latest still carries a leading "v"
    [InlineData("1.4.2", "1.5.0-rc1")] // pre-release suffix on latest is ignored (core compares)
    public void IsUpdateAvailable_True_WhenLatestIsStrictlyNewer(string current, string latest)
    {
        Assert.True(SemVerComparer.IsUpdateAvailable(current, latest));
    }

    [Theory]
    [InlineData("1.5.0", "1.5.0")] // equal
    [InlineData("1.5.0", "1.4.2")] // latest older
    [InlineData("2.0.0", "1.9.9")] // latest older across major
    public void IsUpdateAvailable_False_WhenLatestIsNotNewer(string current, string latest)
    {
        Assert.False(SemVerComparer.IsUpdateAvailable(current, latest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsUpdateAvailable_False_WhenLatestMissing(string? latest)
    {
        Assert.False(SemVerComparer.IsUpdateAvailable("1.4.2", latest));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("DEV")]
    [InlineData(" dev ")]
    public void IsUpdateAvailable_False_ForDevBuilds(string current)
    {
        // Local/dev builds report "dev" — never nag them even when a real release exists.
        Assert.False(SemVerComparer.IsUpdateAvailable(current, "9.9.9"));
    }

    [Theory]
    [InlineData("not-a-version", "1.5.0")]
    [InlineData("1.4.2", "garbage")]
    [InlineData(null, "1.5.0")]
    public void IsUpdateAvailable_False_OnUnparseableInput(string? current, string latest)
    {
        // Fail-safe: anything we can't confidently compare resolves to "no update".
        Assert.False(SemVerComparer.IsUpdateAvailable(current, latest));
    }
}
