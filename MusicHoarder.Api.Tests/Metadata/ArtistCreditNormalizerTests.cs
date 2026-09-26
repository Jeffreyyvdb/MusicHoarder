using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Tests.Metadata;

public class ArtistCreditNormalizerTests
{
    [Theory]
    [InlineData("Kanye West feat. Kid Cudi", new[] { "Kanye West", "Kid Cudi" })]
    [InlineData("Domo Genesis Ft Tyler The Creator", new[] { "Domo Genesis", "Tyler The Creator" })]
    [InlineData("A featuring B", new[] { "A", "B" })]
    [InlineData("A with B", new[] { "A", "B" })]
    public void SplitArtists_FeaturingNeverReturnsTheDelimiterAsAnArtist(string credit, string[] expected)
    {
        // Regex.Split returns captured groups as elements: a capturing featuring group made "feat."
        // an artist, and the credit split wrote "Kanye West; feat.; Kid Cudi" into ARTISTS.
        Assert.Equal(expected, ArtistCreditNormalizer.SplitArtists(credit));
    }

    [Theory]
    [InlineData("Nas (featuring AZ)", new[] { "Nas", "AZ" })]
    [InlineData("Nas (feat. AZ)", new[] { "Nas", "AZ" })]
    [InlineData("Nas [ft. AZ]", new[] { "Nas", "AZ" })]
    [InlineData("Nas (ft AZ)", new[] { "Nas", "AZ" })]
    [InlineData("Nas ( with AZ )", new[] { "Nas", "AZ" })]
    [InlineData("A (feat. B & C)", new[] { "A", "B & C" })]
    public void SplitArtists_SplitsBracketedFeaturingClauses(string credit, string[] expected)
    {
        Assert.Equal(expected, ArtistCreditNormalizer.SplitArtists(credit));
    }

    [Theory]
    [InlineData("Nas (featuring AZ)", "Nas")]
    [InlineData("Nas [feat. AZ]", "Nas")]
    [InlineData("Future (Live)", "Future (Live)")]
    [InlineData("Hef met Jayh", "Hef met Jayh")]
    [InlineData("Florence + the Machine", "Florence + the Machine")]
    public void GetPrimaryArtist_OnlyBracketedFeaturingChangesTheLead(string credit, string expected)
    {
        // Ambiguous joiners stay out of the routing split: GetPrimaryArtist picks destination folders.
        Assert.Equal(expected, ArtistCreditNormalizer.GetPrimaryArtist(credit));
    }

    [Theory]
    [InlineData("Nas (featuring AZ)", true)]
    [InlineData("Nas [ft AZ]", true)]
    [InlineData("Nas feat. AZ", true)]
    [InlineData("Future (Live)", false)]
    [InlineData("Simon & Garfunkel", false)]
    public void HasFeaturingDelimiter_RecognizesBracketedForm(string credit, bool expected)
    {
        Assert.Equal(expected, ArtistCreditNormalizer.HasFeaturingDelimiter(credit));
    }

    [Theory]
    [InlineData("2Pac + Outlawz", new[] { "2Pac", "Outlawz" })]
    [InlineData("50 Cent and Olivia", new[] { "50 Cent", "Olivia" })]
    [InlineData("Hef met Jayh", new[] { "Hef", "Jayh" })]
    [InlineData("Hef en Jayh", new[] { "Hef", "Jayh" })]
    [InlineData("A vs. B", new[] { "A", "B" })]
    [InlineData("Metallica", new[] { "Metallica" })]
    [InlineData("Hefmet Jayh", new[] { "Hefmet Jayh" })]
    public void SplitOnAmbiguousJoiners_SplitsOnlyWholeWordJoiners(string credit, string[] expected)
    {
        Assert.Equal(expected, ArtistCreditNormalizer.SplitOnAmbiguousJoiners(credit));
    }

    [Theory]
    [InlineData("Hef met Jayh", new[] { "Hef", "Jayh" }, true)]
    [InlineData("2Pac + Outlawz", new[] { "2Pac", "Outlawz" }, true)]
    [InlineData("50 Cent and Olivia", new[] { "50 Cent", "Olivia" }, true)]
    [InlineData("Anuel AA + Bad Bunny", new[] { "Anuel AA", "Bad Bunny" }, true)]
    [InlineData("HEF met JAYH", new[] { "Hef", "Jayh" }, true)]
    [InlineData("Hef", new[] { "Hef", "Jayh" }, false)]
    [InlineData("Jayh met Hef", new[] { "Hef", "Jayh" }, false)]
    [InlineData("Hef met Jayhawk", new[] { "Hef", "Jayh" }, false)]
    [InlineData("Simon & Garfunkel", new[] { "Simon & Garfunkel" }, false)]
    public void IsJoinedCreditOf_RecognizesJoinPhrasesInAnyLanguage(string credit, string[] parts, bool expected)
    {
        Assert.Equal(expected, ArtistCreditNormalizer.IsJoinedCreditOf(credit, parts));
    }

    [Theory]
    [InlineData("feat.", true)]
    [InlineData(" FT ", true)]
    [InlineData("featuring", true)]
    [InlineData("with", true)]
    [InlineData("Kid Cudi", false)]
    [InlineData("Feat Kid", false)]
    public void IsDelimiterToken_MatchesOnlyABareDelimiter(string segment, bool expected)
    {
        Assert.Equal(expected, ArtistCreditNormalizer.IsDelimiterToken(segment));
    }

    [Theory]
    [InlineData("Hef met Jayh", new[] { "Hef", "Jayh" }, "Hef")]
    [InlineData("2Pac + Outlawz", new[] { "2Pac", "Outlawz", "Big Syke" }, "2Pac")]
    [InlineData("2Pac + Outlawz", new[] { "2Pac", "feat.", "Outlawz" }, "2Pac")]
    [InlineData("Hef", new[] { "Hef", "Jayh" }, null)]
    [InlineData("Kanye West & JAY-Z", new[] { "Kanye West", "JAY-Z" }, null)]
    [InlineData("Simon & Garfunkel", new[] { "Simon & Garfunkel" }, null)]
    [InlineData("Outlawz + 2Pac", new[] { "2Pac", "Outlawz" }, null)]
    [InlineData(null, new[] { "Hef", "Jayh" }, null)]
    public void LeadOfJoinedCredit_ResolvesTheOldMapperShapeIncludingAPrefix(
        string? albumArtist, string[] parts, string? expected)
    {
        // Only what SplitArtists leaves whole: a credit it splits was never this bug's output, and
        // GetPrimaryArtist already routes it.
        Assert.Equal(expected, ArtistCreditNormalizer.LeadOfJoinedCredit(albumArtist, parts));
    }
}
