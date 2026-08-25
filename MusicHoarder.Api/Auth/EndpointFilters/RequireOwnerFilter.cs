namespace MusicHoarder.Api.Auth.EndpointFilters;

/// <summary>
/// Rejects requests where the authenticated user is not an Owner. Apply with
/// <see cref="RouteHandlerBuilderExtensions.RequireOwner"/>.
/// </summary>
public sealed class RequireOwnerFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserAccessor>();
        if (accessor.User is null)
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!accessor.User.IsOwner)
            return Results.Json(new { error = "owner_required" }, statusCode: StatusCodes.Status403Forbidden);

        return await next(context);
    }
}

/// <summary>
/// Rejects anonymous and demo callers, but lets any real account (Owner or Friend) through. Apply
/// with <see cref="RouteHandlerBuilderExtensions.RequireRealAccount"/>.
///
/// <para>
/// For endpoints that only ever act on the caller's own credentials — passkey enrollment is the
/// case this exists for. A friend has to be able to enrol a passkey, otherwise passkey sign-in on
/// the native client is owner-only; the endpoints behind this filter read
/// <c>accessor.User.Id</c> and never take a user id from the request, so widening them grants a
/// friend nothing beyond their own account. The demo stays out because its credentials are shared
/// by every visitor.
/// </para>
/// </summary>
public sealed class RequireRealAccountFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserAccessor>();
        if (accessor.User is null)
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        if (accessor.User.IsDemo)
            return Results.Json(new { error = "demo_read_only" }, statusCode: StatusCodes.Status403Forbidden);

        return await next(context);
    }
}

public static class RouteHandlerBuilderExtensions
{
    public static RouteHandlerBuilder RequireOwner(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RequireOwnerFilter>();

    public static RouteGroupBuilder RequireOwner(this RouteGroupBuilder builder) =>
        builder.AddEndpointFilter<RequireOwnerFilter>();

    public static RouteHandlerBuilder RequireRealAccount(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RequireRealAccountFilter>();
}
