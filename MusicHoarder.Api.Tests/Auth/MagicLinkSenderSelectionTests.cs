using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Composition;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// The startup banner and the /api/auth/request-link <c>magicLinkInLogs</c> flag both key off
/// which <see cref="IMagicLinkSender"/> the composition root picks, so pin the selection rule:
/// blank Resend key → console fallback (links written to the logs), key set → Resend.
/// </summary>
public class MagicLinkSenderSelectionTests
{
    private static IMagicLinkSender ResolveSender(string? resendApiKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resend:ApiKey"] = resendApiKey,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddMusicHoarderServices();

        return services.BuildServiceProvider().GetRequiredService<IMagicLinkSender>();
    }

    [Fact]
    public void Blank_resend_key_selects_console_fallback()
    {
        var sender = ResolveSender(resendApiKey: null);

        Assert.IsType<ConsoleMagicLinkSender>(sender);
        Assert.True(sender.IsConsoleFallback);
    }

    [Fact]
    public void Configured_resend_key_selects_resend_sender()
    {
        var sender = ResolveSender(resendApiKey: "re_test_key");

        Assert.IsType<ResendMagicLinkSender>(sender);
        Assert.False(sender.IsConsoleFallback);
    }
}
