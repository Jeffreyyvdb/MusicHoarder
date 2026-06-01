using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Composition;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Auth;

public class ResolveRelyingPartyTests
{
    [Fact]
    public void Apex_public_base_url_also_allows_www_origin()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions(),
            new FrontendOptions { PublicBaseUrl = "https://musichoarder.app" });

        Assert.Equal("musichoarder.app", rpId);
        Assert.Contains("https://musichoarder.app", origins);
        Assert.Contains("https://www.musichoarder.app", origins);
    }

    [Fact]
    public void Www_public_base_url_also_allows_apex_origin()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions(),
            new FrontendOptions { PublicBaseUrl = "https://www.musichoarder.app" });

        // RpId is derived from the host as-is; the apex sibling is still allowed as an origin so
        // either host passes verification.
        Assert.Contains("https://www.musichoarder.app", origins);
        Assert.Contains("https://musichoarder.app", origins);
    }

    [Fact]
    public void Explicit_rpId_expands_apex_and_www_siblings()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions { RpId = "musichoarder.app" },
            new FrontendOptions { PublicBaseUrl = "https://www.musichoarder.app" });

        Assert.Equal("musichoarder.app", rpId);
        Assert.Contains("https://musichoarder.app", origins);
        Assert.Contains("https://www.musichoarder.app", origins);
    }

    [Fact]
    public void Non_default_port_is_preserved_on_sibling()
    {
        var (_, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions { RpId = "musichoarder.app" },
            new FrontendOptions { PublicBaseUrl = "https://musichoarder.app:8443" });

        Assert.Contains("https://musichoarder.app:8443", origins);
        Assert.Contains("https://www.musichoarder.app:8443", origins);
    }

    [Fact]
    public void Localhost_dev_origin_is_left_untouched()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions(),
            new FrontendOptions { PublicBaseUrl = "http://localhost:3000" });

        Assert.Equal("localhost", rpId);
        Assert.Contains("http://localhost:3000", origins);
        // "www.localhost" is meaningless — only apex/www of the registrable domain are expanded.
        Assert.DoesNotContain("http://www.localhost:3000", origins);
    }

    [Fact]
    public void Bare_api_boot_falls_back_to_localhost()
    {
        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions(),
            new FrontendOptions { PublicBaseUrl = "" });

        Assert.Equal("localhost", rpId);
        Assert.Contains("https://localhost", origins);
    }
}
