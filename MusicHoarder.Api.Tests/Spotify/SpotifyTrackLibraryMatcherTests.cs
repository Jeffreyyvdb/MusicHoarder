using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

/// <summary>
/// Exercises the pure matching algorithm in isolation, straight against
/// <see cref="SpotifyTrackLibraryMatcher"/> and its <see cref="TrackIndex"/> — no database, Spotify
/// API stub, or comparison service required. These pin the exact-id → normalized → fuzzy priority
/// ladder that <see cref="SpotifyLibraryComparisonService"/> relies on.
/// </summary>
public class SpotifyTrackLibraryMatcherTests
{
    #region Normalize tests

    [Theory]
    [InlineData("Hello World", "hello world")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("Song (feat. Artist)", "song")]
    [InlineData("Song (Remix)", "song")]
    [InlineData("Song [Official Video]", "song")]
    [InlineData("Song ft. Someone", "song")]
    [InlineData("Song feat. Someone", "song")]
    [InlineData("Hello, World!", "hello world")]
    [InlineData("  Extra   Spaces  ", "extra spaces")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("Song (feat. X) [Remix]", "song")]
    public void Normalize_ProducesExpectedOutput(string? input, string expected)
    {
        Assert.Equal(expected, SpotifyTrackLibraryMatcher.Normalize(input));
    }

    #endregion

    #region FindBestMatch tests — exact Spotify ID

    [Fact]
    public void FindBestMatch_ExactSpotifyIdMatch_ReturnsInLibraryWith100Percent()
    {
        var likedSong = MakeLikedSong("spotify:123", "Some Artist", "Some Title");
        var index = BuildIndex(new TrackIndexEntry(1, "spotify:123", "Different Artist", "Different Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.NotNull(matched);
        Assert.Equal(1, matched.Id);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void FindBestMatch_ExactSpotifyIdMatch_CaseInsensitive()
    {
        var likedSong = MakeLikedSong("ABC123", "Artist", "Title");
        var index = BuildIndex(new TrackIndexEntry(1, "abc123", "Artist", "Title", EnrichmentStatus.Pending));

        var (status, _, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.Equal(1.0, confidence);
    }

    #endregion

    #region FindBestMatch tests — normalized match

    [Fact]
    public void FindBestMatch_NormalizedArtistAndTitleMatch_ReturnsInLibrary()
    {
        var likedSong = MakeLikedSong("no-match-id", "Artist (feat. Someone)", "Song Title (Remix)");
        var index = BuildIndex(new TrackIndexEntry(42, null, "Artist", "Song Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.NotNull(matched);
        Assert.Equal(42, matched.Id);
        Assert.Equal(0.95, confidence);
    }

    [Fact]
    public void FindBestMatch_NormalizedMatch_StripsFeaturingAndParentheses()
    {
        var likedSong = MakeLikedSong("no-id", "Drake feat. Lil Wayne", "God's Plan (Official Video)");
        var index = BuildIndex(new TrackIndexEntry(10, null, "Drake", "God's Plan", EnrichmentStatus.Matched));

        var (status, _, _) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
    }

    #endregion

    #region FindBestMatch tests — fuzzy match

    [Fact]
    public void FindBestMatch_FuzzyMatch_ReturnsPossibleMatchWithScore()
    {
        var likedSong = MakeLikedSong("no-id", "Kendrick Lamar", "HUMBLE");
        var index = BuildIndex(new TrackIndexEntry(99, null, "Kendrik Lamar", "HUMBLE", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.PossibleMatch, status);
        Assert.NotNull(matched);
        Assert.Equal(99, matched.Id);
        Assert.NotNull(confidence);
        Assert.True(confidence >= 0.85);
    }

    [Fact]
    public void FindBestMatch_FuzzyMatch_PicksBestCandidate()
    {
        var likedSong = MakeLikedSong("no-id", "The Weeknd", "Blinding Lights");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, "The Weekend", "Blinding Lights", EnrichmentStatus.Matched),
            new TrackIndexEntry(2, null, "The Weeknd", "Blinding Light", EnrichmentStatus.Matched));

        var (status, matched, _) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.PossibleMatch, status);
        Assert.NotNull(matched);
    }

    #endregion

    #region FindBestMatch tests — no match

    [Fact]
    public void FindBestMatch_NoMatch_ReturnsNotInLibrary()
    {
        var likedSong = MakeLikedSong("no-id", "Completely Unknown Artist", "Totally Different Song");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, "Artist A", "Song A", EnrichmentStatus.Matched),
            new TrackIndexEntry(2, null, "Artist B", "Song B", EnrichmentStatus.Pending));

        var (status, matched, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, status);
        Assert.Null(matched);
        Assert.Null(confidence);
    }

    [Fact]
    public void FindBestMatch_EmptyIndex_ReturnsNotInLibrary()
    {
        var likedSong = MakeLikedSong("some-id", "Artist", "Title");
        var index = BuildIndex();

        var (status, matched, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, status);
        Assert.Null(matched);
        Assert.Null(confidence);
    }

    #endregion

    #region FindBestMatch tests — priority ordering

    [Fact]
    public void FindBestMatch_PrefersExactIdOverNormalizedMatch()
    {
        var likedSong = MakeLikedSong("spotify:exact", "Artist", "Title");
        var index = BuildIndex(
            new TrackIndexEntry(1, "spotify:exact", "Different", "Different", EnrichmentStatus.Pending),
            new TrackIndexEntry(2, null, "Artist", "Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.Equal(1, matched!.Id);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void FindBestMatch_PrefersNormalizedOverFuzzy()
    {
        var likedSong = MakeLikedSong("no-id", "Artist Name", "Song Title");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, "Artist Name", "Song Title", EnrichmentStatus.Matched),
            new TrackIndexEntry(2, null, "Artist Nme", "Song Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.Equal(1, matched!.Id);
        Assert.Equal(0.95, confidence);
    }

    #endregion

    #region Edge cases

    [Fact]
    public void FindBestMatch_SkipsEntriesWithNullArtistOrTitle_InFuzzyMatching()
    {
        var likedSong = MakeLikedSong("no-id", "Artist", "Title");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, null, "Title", EnrichmentStatus.Pending),
            new TrackIndexEntry(2, null, "Artist", null, EnrichmentStatus.Pending));

        var (status, _, _) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, status);
    }

    [Fact]
    public void FindBestMatch_MatchedTrack_IncludesEnrichmentStatus()
    {
        var likedSong = MakeLikedSong("spotify:x", "A", "B");
        var index = BuildIndex(new TrackIndexEntry(5, "spotify:x", "A", "B", EnrichmentStatus.NeedsReview));

        var (_, matched, _) = SpotifyTrackLibraryMatcher.FindBestMatch(likedSong, index);

        Assert.NotNull(matched);
        Assert.Equal("NeedsReview", matched.EnrichmentStatus);
    }

    #endregion

    #region Helpers

    private static SpotifyTrackItem MakeLikedSong(string spotifyId, string artist, string title) =>
        new(spotifyId, title, artist, "Album", null, 200000, DateTime.UtcNow);

    private static TrackIndex BuildIndex(params TrackIndexEntry[] entries) =>
        new(entries);

    #endregion
}
