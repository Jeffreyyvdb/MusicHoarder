using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

/// <summary>
/// Picking a clean edit's explicit edition out of a catalog search: only the same song (title, artists,
/// identity qualifiers, length) qualifies, never a live take or a different cut.
/// </summary>
public class ExplicitVersionMatcherTests
{
    private static SpotifyCatalogTrack Track(
        string id, bool isExplicit, string title = "Money Trees", string artist = "Kendrick Lamar, Jay Rock",
        int durationMs = 386_000, string? artists = "Kendrick Lamar; Jay Rock") =>
        new(id, title, artist, "good kid, m.A.A.d city", 2012, 5, durationMs, null, Artists: artists, Explicit: isExplicit);

    [Fact]
    public void FindsTheExplicitEdition_OfACleanEdit()
    {
        var clean = Track("clean", isExplicit: false);

        var edition = ExplicitVersionMatcher.FindExplicitEdition(clean, [clean, Track("explicit", isExplicit: true, durationMs: 386_400)]);

        Assert.Equal("explicit", edition?.Id);
    }

    [Fact]
    public void AnExplicitTrack_IsLeftAlone()
    {
        var track = Track("explicit", isExplicit: true);

        Assert.Null(ExplicitVersionMatcher.FindExplicitEdition(track, [Track("other", isExplicit: true)]));
    }

    [Fact]
    public void ACleanMarkerOnTheTitle_StillMatches()
    {
        var clean = Track("clean", isExplicit: false, title: "Money Trees (Clean)");

        Assert.Equal("explicit", ExplicitVersionMatcher.FindExplicitEdition(clean, [Track("explicit", isExplicit: true)])?.Id);
        var hyphenated = Track("clean", isExplicit: false, title: "Money Trees - Edited Version");
        Assert.Equal("explicit", ExplicitVersionMatcher.FindExplicitEdition(hyphenated, [Track("explicit", isExplicit: true)])?.Id);
    }

    [Theory]
    [InlineData("Money Trees - Live", "Kendrick Lamar, Jay Rock", 386_000)] // a different recording
    [InlineData("Money Trees (Live)", "Kendrick Lamar, Jay Rock", 386_000)]
    [InlineData("Swimming Pools", "Kendrick Lamar, Jay Rock", 386_000)] // a different song
    [InlineData("Money Trees", "Someone Else", 386_000)] // a cover
    [InlineData("Money Trees", "Kendrick Lamar, Jay Rock", 240_000)] // a different cut (radio edit)
    [InlineData("Money Trees", "Kendrick Lamar, Jay Rock", 0)] // unknown length proves nothing
    public void OnlyTheSameSong_Qualifies(string title, string artist, int durationMs)
    {
        var clean = Track("clean", isExplicit: false);

        Assert.Null(ExplicitVersionMatcher.FindExplicitEdition(
            clean, [Track("explicit", isExplicit: true, title: title, artist: artist, durationMs: durationMs)]));
    }

    [Fact]
    public void TheClosestLength_Wins()
    {
        var clean = Track("clean", isExplicit: false);

        var edition = ExplicitVersionMatcher.FindExplicitEdition(clean,
        [
            Track("farther", isExplicit: true, durationMs: 388_500),
            Track("closer", isExplicit: true, durationMs: 385_800),
        ]);

        Assert.Equal("closer", edition?.Id);
    }

    [Fact]
    public void SearchQuery_UsesTheLeadArtist_AndDropsTheCleanMarker()
    {
        var clean = Track("clean", isExplicit: false, title: "Money Trees (Clean)");

        Assert.Equal("track:Money Trees artist:Kendrick Lamar", ExplicitVersionMatcher.SearchQuery(clean));
    }
}
