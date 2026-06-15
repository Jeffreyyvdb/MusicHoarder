using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class CandidateTextMatchTests
{
    [Fact]
    public void AllTokensPresent_IsFullContainment()
    {
        Assert.Equal(1.0, CandidateTextMatch.Containment("This Is the Life", "Amy Macdonald This Is the Life"));
        Assert.True(CandidateTextMatch.AllPresent("Amy Macdonald", "Amy Macdonald This Is the Life"));
    }

    [Fact]
    public void OrderIndependent_ArtistAndTitleEitherSideOfFilename()
    {
        // "20-luie_mannen-hef" cleans to "luie mannen hef" — title "Luie Mannen" and artist "Hef" appear
        // in either order, and both must corroborate regardless of which side of the dash they were on.
        const string haystack = "luie mannen hef";
        Assert.Equal(1.0, CandidateTextMatch.Containment("Luie Mannen", haystack));
        Assert.Equal(1.0, CandidateTextMatch.Containment("Hef", haystack));
    }

    [Fact]
    public void PartialOverlap_IsFractional()
    {
        // One of two tokens present → 0.5.
        Assert.Equal(0.5, CandidateTextMatch.Containment("People Help", "people the"));
    }

    [Fact]
    public void AbsentCandidate_IsZero()
    {
        Assert.Equal(0.0, CandidateTextMatch.Containment("Penny Lover", "birdy people help the people"));
    }

    [Fact]
    public void ShortToken_MatchesWholeTokenNotSubstring()
    {
        // "hef" must not match the inside of "chef"/"heft" — whole-token matching only.
        Assert.Equal(0.0, CandidateTextMatch.Containment("hef", "the chef heft"));
    }

    [Fact]
    public void EmptyOrPunctuationOnly_IsZero()
    {
        Assert.Equal(0.0, CandidateTextMatch.Containment(null, "anything"));
        Assert.Equal(0.0, CandidateTextMatch.Containment("Hef", null));
        Assert.Equal(0.0, CandidateTextMatch.Containment("", "anything"));
    }
}
