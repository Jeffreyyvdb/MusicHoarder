namespace MusicHoarder.Api.Auth;

/// <summary>
/// WebAuthn / FIDO2 relying-party configuration. All values are optional: when left blank they
/// are derived from <see cref="Options.FrontendOptions.PublicBaseUrl"/> (the browser origin
/// Aspire already injects), so dev and single-domain deployments need no extra config. Set these
/// only to override (e.g. multiple allowed origins).
/// </summary>
public class WebAuthnOptions
{
    public const string SectionName = "WebAuthn";

    /// <summary>
    /// Relying-party id — the registrable domain (e.g. <c>localhost</c> or <c>app.example.com</c>),
    /// host only, no scheme/port. Empty → derived from the frontend public base URL host.
    /// </summary>
    public string RpId { get; set; } = string.Empty;

    /// <summary>Human-friendly relying-party name shown by some authenticators.</summary>
    public string RpName { get; set; } = "MusicHoarder";

    /// <summary>
    /// Allowed full origins (scheme + host + port) that ceremonies may come from. Empty → the
    /// frontend public base URL is used as the sole allowed origin.
    /// </summary>
    public List<string> Origins { get; set; } = [];
}
