using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class GradingIssueJsonTests
{
    [Fact]
    public void Parse_ReadsTheCamelCaseWireFormat()
    {
        var issues = GradingIssueJson.Parse(
            """[{"code":"unsupported_identity","severity":"high","detail":"why"},{"code":"low_confidence","severity":"medium"}]""");

        Assert.Equal(2, issues.Count);
        Assert.Equal("unsupported_identity", issues[0].Code);
        Assert.Equal("high", issues[0].Severity);
        Assert.Equal("why", issues[0].Detail);
        Assert.Null(issues[1].Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{\"code\":\"an-object-not-a-list\"}")]
    public void Parse_EmptyOrMalformedPayload_ReadsAsNoIssues(string? json)
    {
        Assert.Empty(GradingIssueJson.Parse(json));
    }

    [Fact]
    public void ParseCodes_SkipsBlankCodes()
    {
        var codes = GradingIssueJson.ParseCodes(
            """[{"code":"low_confidence","severity":"low"},{"code":"","severity":"low"},{"code":"  ","severity":"low"}]""");

        Assert.Equal(["low_confidence"], codes);
    }
}
