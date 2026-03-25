namespace MusicHoarder.Api.Options;

/// <summary>
/// Public URL of the browser-facing app (e.g. Next.js). Used after Spotify OAuth to redirect users back from the API callback.
/// </summary>
public class FrontendOptions
{
    public const string SectionName = "Frontend";

    /// <summary>
    /// Origin only, no trailing slash (e.g. https://app.example.com). Empty disables browser redirects after OAuth (JSON responses only).
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;
}
