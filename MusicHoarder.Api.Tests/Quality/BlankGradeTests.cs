using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

/// <summary>
/// A blank grade is an empty model reply the old parser stored as an "ungradeable" verdict. It must be
/// told apart from a genuine "ungradeable" verdict in both directions: a blank is regraded, a genuine
/// one never is — otherwise the sweep would regrade the same song on every pass.
/// </summary>
public class BlankGradeTests
{
    // Stored replies from production (openai/gpt-oss-20b leaking its reasoning channels).
    private const string AnalysisOnly =
        """{"analysis":"We need to grade the final chosen metadata ... So just looks_correct. Score 95. Let's output. }" }""";

    private const string GradeUnderFinal =
        """{"analysis":"We need to grade ... Let's do. }","final":"{\"score\":95,\"verdict\":\"excellent\",\"summary\":\"Corroborated.\",\"issues\":[]}"}""";

    private const string GenuineUngradeable = """{"score":0,"verdict":"ungradeable"}""";

    [Theory]
    [InlineData(AnalysisOnly)]
    [InlineData("""{ }""")]
    [InlineData("""{ "analysis": [ ]}""")]
    [InlineData("""{"commentary to=assistant{" : "analysis"}""")]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyReplyStoredAsUngradeable_IsBlank(string? raw)
    {
        Assert.True(BlankGrade.IsBlank(SongQualityVerdict.Ungradeable, summary: null, raw));
    }

    [Fact]
    public void ReplyWhoseGradeTheOldParserMissed_IsBlank()
    {
        // The reply held an Excellent grade under "final"; the stored Ungradeable row is wrong.
        Assert.True(BlankGrade.IsBlank(SongQualityVerdict.Ungradeable, summary: null, GradeUnderFinal));
    }

    [Fact]
    public void GenuineSummarylessUngradeableVerdict_IsNotBlank()
    {
        // Re-reads as Ungradeable: this is the model's real answer, so it must never be regraded.
        Assert.False(BlankGrade.IsBlank(SongQualityVerdict.Ungradeable, summary: null, GenuineUngradeable));
    }

    [Fact]
    public void UngradeableWithASummary_IsNotBlank()
    {
        Assert.False(BlankGrade.IsBlank(SongQualityVerdict.Ungradeable, "No owned songs to compare.", AnalysisOnly));
    }

    [Theory]
    [InlineData(SongQualityVerdict.Wrong)]
    [InlineData(SongQualityVerdict.Questionable)]
    [InlineData(SongQualityVerdict.Good)]
    [InlineData(SongQualityVerdict.Excellent)]
    public void AnyOtherVerdict_IsNotBlank(SongQualityVerdict verdict)
    {
        Assert.False(BlankGrade.IsBlank(verdict, summary: null, AnalysisOnly));
    }

    [Fact]
    public void ReplyCutAtTheStorageCap_IsTrustedAsStored()
    {
        // The grade may lie past the cut, so a cut reply that no longer re-reads can't prove the row
        // blank — trusting it keeps a long genuine "ungradeable" reply from being regraded forever.
        var cut = ("""{"analysis":" """ + new string('x', BlankGrade.MaxStoredResponseChars))[..BlankGrade.MaxStoredResponseChars];

        Assert.False(BlankGrade.IsBlank(SongQualityVerdict.Ungradeable, summary: null, cut));
    }

    [Fact]
    public void EntityOverloads_ReadTheSameFields()
    {
        Assert.True(new SongQualityGrade { Verdict = SongQualityVerdict.Ungradeable, RawResponseJson = AnalysisOnly }.IsBlank());
        Assert.False(new SongQualityGrade { Verdict = SongQualityVerdict.Ungradeable, RawResponseJson = GenuineUngradeable }.IsBlank());
        Assert.True(new CanonicalAlbumQualityGrade { Verdict = SongQualityVerdict.Ungradeable, RawResponseJson = AnalysisOnly }.IsBlank());
        Assert.False(new CanonicalAlbumQualityGrade { Verdict = SongQualityVerdict.Ungradeable, RawResponseJson = GenuineUngradeable }.IsBlank());
    }
}
