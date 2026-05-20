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
}

public sealed class SessionCookieService : ISessionCookieService
{
    private const string ProtectorPurpose = "MusicHoarder.SessionCookie.v1";

    private readonly IDataProtector _protector;
    private readonly IOptionsMonitor<AuthOptions> _options;

    public SessionCookieService(IDataProtectionProvider dpProvider, IOptionsMonitor<AuthOptions> options)
    {
        _protector = dpProvider.CreateProtector(ProtectorPurpose);
        _options = options;
    }

    public string CookieName => _options.CurrentValue.CookieName;

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

    public void Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions
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
