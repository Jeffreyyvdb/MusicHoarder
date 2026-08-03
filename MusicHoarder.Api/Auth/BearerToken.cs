namespace MusicHoarder.Api.Auth;

/// <summary>
/// Reads the bearer access token native clients send instead of the session cookie. The token
/// value is the same data-protection-wrapped session id the cookie carries, so both transports
/// resolve through <see cref="ISessionCookieService.Unprotect"/> and share revocation + sliding
/// lifetime via the server-side <see cref="Session"/> row.
/// </summary>
public static class BearerToken
{
    private const string Scheme = "Bearer ";

    /// <summary>Returns the raw token from the Authorization header, or null when absent.</summary>
    public static string? TryRead(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header[Scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
