using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Observability;

/// <summary>
/// Gives every request one id that both halves of the app log under, so a problem report (Looping,
/// <c>frontend/src/lib/looping.svelte.ts</c>) whose network entry names an <c>x-request-id</c> leads
/// to the API's log lines for that call as well as the frontend's.
///
/// <para>
/// The frontend server mints the id (<c>#lib/server/looping</c>) and its <c>/api/mh</c> proxy
/// forwards it; a request that arrives without one (or with one that is not a plain token) gets a
/// fresh id here. The id is echoed as the <c>x-request-id</c> response header and opened as a log
/// scope (<c>rid</c>), which the console formatter prints on each line (<c>IncludeScopes</c> and
/// <c>SingleLine</c> in appsettings.json) — a line-based search of the container logs is how reports
/// find them.
/// </para>
/// </summary>
public sealed partial class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
{
    public const string HeaderName = "x-request-id";
    public const string ScopeKey = "rid";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = Resolve(context.Request.Headers[HeaderName].ToString());
        context.Response.Headers[HeaderName] = id;
        // Exported logs carry no scopes, so the id rides on the request's span: a Looping report
        // finds its trace by it, and the trace finds its logs.
        Activity.Current?.SetTag("request.id", id);
        // A message-template scope, not a dictionary: the simple console formatter prints a scope
        // with ToString(), which for a dictionary is only its type name.
        using (logger.BeginScope("rid:{" + ScopeKey + "}", id))
        {
            await next(context);
        }
    }

    /// <summary>The incoming id when it is a sane token (what the frontend sends), else a new one.</summary>
    public static string Resolve(string? incoming) =>
        incoming is not null && ValidId().IsMatch(incoming) ? incoming : NewId();

    private static string NewId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_');

    // The same shape the frontend's helper accepts: no spaces or separators that could forge a log
    // line. ASCII only, and `\z` rather than `$`, which in .NET also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{6,128}\z")]
    private static partial Regex ValidId();
}
