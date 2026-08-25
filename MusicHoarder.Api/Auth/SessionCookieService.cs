using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace MusicHoarder.Api.Auth;

public interface ISessionCookieService
{
    /// <summary>Builds a signed cookie value for the given session id.</summary>
    string Protect(Guid sessionId);

    /// <summary>Returns the session id if the cookie value is valid, otherwise null.</summary>
    Guid? Unprotect(string cookieValue);

    /// <summary>Writes the session cookie with the right flags.</summary>
    void Write(HttpContext context, Guid sessionId);

    /// <summary>Clears the session cookie.</summary>
    void Clear(HttpContext context);

    /// <summary>The cookie name in use (per <see cref="AuthOptions.CookieName"/>).</summary>
    string CookieName { get; }

    /// <summary>The parked-accounts cookie name (<see cref="CookieName"/> + "_alts").</summary>
    string AltsCookieName { get; }

    /// <summary>
    /// Reads the parked session ids, newest first. Missing/corrupt cookie reads as empty (and a
    /// corrupt one is cleared).
    /// </summary>
    IReadOnlyList<Guid> ReadAlts(HttpContext context);

    /// <summary>Writes the parked session ids (newest first); an empty list clears the cookie.</summary>
    void WriteAlts(HttpContext context, IReadOnlyList<Guid> sessionIds);

    /// <summary>Clears the parked-accounts cookie.</summary>
    void ClearAlts(HttpContext context);
}

public sealed class SessionCookieService : ISessionCookieService
{
    private const string ProtectorPurpose = "MusicHoarder.SessionCookie.v1";

    // Distinct purpose so an alts blob can never be replayed as a session cookie (or vice versa).
    private const string AltsProtectorPurpose = "MusicHoarder.SessionAltsCookie.v1";

    private readonly IDataProtector _protector;
    private readonly IDataProtector _altsProtector;
    private readonly IOptionsMonitor<AuthOptions> _options;

    public SessionCookieService(IDataProtectionProvider dpProvider, IOptionsMonitor<AuthOptions> options)
    {
        _protector = dpProvider.CreateProtector(ProtectorPurpose);
        _altsProtector = dpProvider.CreateProtector(AltsProtectorPurpose);
        _options = options;
    }

    public string CookieName => _options.CurrentValue.CookieName;

    public string AltsCookieName => CookieName + "_alts";

    public string Protect(Guid sessionId) => _protector.Protect(sessionId.ToString("N"));

    public Guid? Unprotect(string cookieValue)
    {
        try
        {
            var raw = _protector.Unprotect(cookieValue);
            return Guid.ParseExact(raw, "N");
        }
        catch
        {
            return null;
        }
    }

    public void Write(HttpContext context, Guid sessionId)
    {
        var value = Protect(sessionId);
        context.Response.Cookies.Append(CookieName, value, BuildCookieOptions(context));
    }

    public void Clear(HttpContext context) => Delete(context, CookieName);

    public IReadOnlyList<Guid> ReadAlts(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(AltsCookieName, out var value) || string.IsNullOrEmpty(value))
            return [];

        try
        {
            var raw = _altsProtector.Unprotect(value);
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => Guid.ParseExact(part, "N"))
                .ToList();
        }
        catch
        {
            // Tampered or from a rotated key ring — same posture as the active cookie: forget it.
            ClearAlts(context);
            return [];
        }
    }

    public void WriteAlts(HttpContext context, IReadOnlyList<Guid> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            ClearAlts(context);
            return;
        }

        var value = _altsProtector.Protect(string.Join(',', sessionIds.Select(id => id.ToString("N"))));
        context.Response.Cookies.Append(AltsCookieName, value, BuildCookieOptions(context));
    }

    public void ClearAlts(HttpContext context) => Delete(context, AltsCookieName);

    private static void Delete(HttpContext context, string cookieName)
    {
        context.Response.Cookies.Delete(cookieName, new CookieOptions
        {
            Path = "/",
            Secure = context.Request.IsHttps,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        });
    }

    private CookieOptions BuildCookieOptions(HttpContext context) => new()
    {
        Path = "/",
        Secure = context.Request.IsHttps,
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Expires = DateTime.UtcNow.AddDays(_options.CurrentValue.SessionLifetimeDays),
    };
}
