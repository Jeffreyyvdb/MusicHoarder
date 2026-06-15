using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class SourceIdentityScorerTests
{
    private const double FuzzyThreshold = 85.0;

    [Fact]
    public void EmbeddedTags_Matching_ScoresHighWithNoWarnings()
    {
        var source = new SongSearchText.Resolved("Amy Macdonald", null, "This Is the Life", null);
        var warnings = new List<string>();

        var score = SourceIdentityScorer.Score(source, "Amy Macdonald", "This Is the Life", FuzzyThreshold, warnings);

        Assert.True(score > 0.95);
        Assert.Empty(warnings);
    }

    [Fact]
    public void EmbeddedTags_ArtistMismatch_EmitsBlockingArtistMismatch()
    {
        var source = new SongSearchText.Resolved("Amy Macdonald", null, "This Is the Life", null);
        var warnings = new List<string>();

        SourceIdentityScorer.Score(source, "Lionel Richie", "This Is the Life", FuzzyThreshold, warnings);

        Assert.Contains("artist_mismatch", warnings);
        Assert.True(MatchWarnings.AnyBlocking(warnings));
    }

    [Fact]
    public void PathDerived_CandidatePresentInFilename_ScoresHighAndIsNonBlocking()
    {
        // Junk positional artist ("slskd"), but the real artist+title are in the filename free-text.
        var source = new SongSearchText.Resolved("slskd", "A", "Amy Macdonald - This Is the Life", null)
        {
            ArtistFromPath = true,
            TitleFromPath = true,
            RawSearchText = "Amy Macdonald This Is the Life",
        };
        var warnings = new List<string>();

        var score = SourceIdentityScorer.Score(source, "Amy Macdonald", "This Is the Life", FuzzyThreshold, warnings);

        Assert.True(score > 0.95);
        Assert.Contains(MatchWarnings.IdentityUnverified, warnings);
        // The junk "slskd" guess must NOT manufacture a blocking warning.
        Assert.DoesNotContain("artist_mismatch", warnings);
        Assert.DoesNotContain("title_mismatch", warnings);
        Assert.False(MatchWarnings.AnyBlocking(warnings));
    }

    [Fact]
    public void PathDerived_CandidateAbsentFromFilename_ScoresLowButStaysNonBlocking()
    {
        var source = new SongSearchText.Resolved("slskd", "B", "Birdy - People Help the People", null)
        {
            ArtistFromPath = true,
            TitleFromPath = true,
            RawSearchText = "Birdy People Help the People",
        };
        var warnings = new List<string>();

        // A wrong fingerprint-style candidate that doesn't appear in the filename.
        var score = SourceIdentityScorer.Score(source, "Lionel Richie", "Penny Lover", FuzzyThreshold, warnings);

        Assert.True(score < 0.5);
        Assert.Contains(MatchWarnings.IdentityUnverified, warnings);
        Assert.False(MatchWarnings.AnyBlocking(warnings));
    }

    [Fact]
    public void PathDerived_TitlePresentArtistAbsent_ScoresMidNonBlocking()
    {
        var source = new SongSearchText.Resolved("slskd", null, "Some Cover - This Is the Life", null)
        {
            ArtistFromPath = true,
            TitleFromPath = true,
            RawSearchText = "This Is the Life",
        };
        var warnings = new List<string>();

        var score = SourceIdentityScorer.Score(source, "Different Artist", "This Is the Life", FuzzyThreshold, warnings);

        // Title present, artist absent → 0.5 (review-worthy, but not vetoed).
        Assert.Equal(0.5, score, 3);
        Assert.False(MatchWarnings.AnyBlocking(warnings));
    }
}
