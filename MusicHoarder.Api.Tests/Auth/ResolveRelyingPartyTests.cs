using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Composition;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Auth;

public class ResolveRelyingPartyTests
{
    [Fact]
    public void Android_apk_key_hash_origin_survives_verbatim()
    {
        // Load-bearing for in-app passkeys: Credential Manager reports an Android caller's origin
        // as `android:apk-key-hash:<base64url sha256>`, and fido2-net-lib compares a non-URL origin
        // as an exact string. If the derivation rewrote it — as it does for http(s) origins, which
        // gain a www sibling — every passkey assertion from the app would fail the origin check.
        const string apkOrigin = "android:apk-key-hash:9oRsCTHMFcQ1eS7Ke0EqfKPr9K4TjxRV5oXQnQ8kQ9A";

        var (rpId, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions { Origins = [apkOrigin] },
            new FrontendOptions { PublicBaseUrl = "https://musichoarder.app" });

        Assert.Contains(apkOrigin, origins);
        // The web origin is untouched: the app's origin is additional, never a replacement.
        Assert.Contains("https://musichoarder.app", origins);
        Assert.Equal("musichoarder.app", rpId);
    }

    [Fact]
    public void Blank_android_origin_adds_nothing()
    {
        // The AppHost always maps WebAuthn__Origins__0, so an unconfigured deployment binds an
        // empty string into slot 0. It must not become an allowed origin.
        var (_, origins) = ServiceCollectionExtensions.ResolveRelyingParty(
            new WebAuthnOptions { Origins = [""] },
            new FrontendOptions { PublicBaseUrl = "https://musichoarder.app" });

        Assert.DoesNotContain("", origins);
        Assert.Contains("https://musichoarder.app", origins);
    }

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
