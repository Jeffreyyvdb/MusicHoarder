using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Quality;

// IsConfigured is the single gate the bulk grade endpoints (503), the auto-sweep, the worker's
// not-configured warning, and the frontend's aiGradingConfigured flag all key off. Pin its behaviour.
public class QualityGradingOptionsTests
{
    [Fact]
    public void IsConfigured_True_WhenEnabledWithKeyAndBaseUrl()
    {
        var opts = new QualityGradingOptions { Enabled = true, ApiKey = "sk-123", BaseUrl = "https://openrouter.ai/api/v1" };
        Assert.True(opts.IsConfigured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsConfigured_False_WhenApiKeyBlank(string key)
    {
        var opts = new QualityGradingOptions { Enabled = true, ApiKey = key, BaseUrl = "https://openrouter.ai/api/v1" };
        Assert.False(opts.IsConfigured);
    }

    [Fact]
    public void IsConfigured_False_WhenDisabled()
    {
        var opts = new QualityGradingOptions { Enabled = false, ApiKey = "sk-123", BaseUrl = "https://openrouter.ai/api/v1" };
        Assert.False(opts.IsConfigured);
    }

    [Fact]
    public void IsConfigured_False_WhenBaseUrlBlank()
    {
        var opts = new QualityGradingOptions { Enabled = true, ApiKey = "sk-123", BaseUrl = "" };
        Assert.False(opts.IsConfigured);
    }

    // Reasoning models share MaxOutputTokens with their chain-of-thought, so the defaults must leave
    // headroom: a bounded reasoning cap that sits comfortably below the output budget, plus retries.
    [Fact]
    public void Defaults_GiveReasoningHeadroomAndRetries()
    {
        var opts = new QualityGradingOptions();

        Assert.Equal(4096, opts.MaxOutputTokens);
        Assert.Equal(2000, opts.ReasoningMaxTokens);
        Assert.True(opts.ReasoningMaxTokens < opts.MaxOutputTokens);
        Assert.Equal(120, opts.TimeoutSeconds);
        Assert.Equal(3, opts.MaxRetries);
    }
}
