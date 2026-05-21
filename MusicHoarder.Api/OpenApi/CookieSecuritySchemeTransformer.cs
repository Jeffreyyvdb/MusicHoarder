using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MusicHoarder.Api.Auth;

namespace MusicHoarder.Api.OpenApi;

/// <summary>
/// Documents the session-cookie auth in the OpenAPI document so Scalar surfaces it and the spec is
/// accurate. Auth is the <c>mh_session</c> httpOnly cookie (no bearer token), so this scheme is an
/// <c>apiKey</c> in a cookie. Browsers won't let Scalar set a Cookie header from its auth box; the
/// cookie is attached automatically on same-origin requests once you log in via the /api/auth
/// endpoints from within Scalar.
/// </summary>
public sealed class CookieSecuritySchemeTransformer(IOptionsMonitor<AuthOptions> authOptions)
    : IOpenApiDocumentTransformer
{
    public const string SchemeId = "SessionCookie";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.AddComponent(SchemeId, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = authOptions.CurrentValue.CookieName,
            Description =
                "Session cookie issued by POST /api/auth/consume (magic-link token) or " +
                "POST /api/auth/demo-login. Because it is httpOnly the browser sends it " +
                "automatically on same-origin requests — log in from within Scalar first; " +
                "you cannot paste a value into the auth box.",
        });

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeId, document)] = [],
        });

        return Task.CompletedTask;
    }
}
