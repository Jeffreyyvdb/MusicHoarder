namespace MusicHoarder.Api.Auth.EndpointFilters;

/// <summary>
/// Rejects a caller that does not hold <paramref name="required"/>. Authorizes against
/// <see cref="CurrentUser.Can"/>, so an <see cref="UserRole.Admin"/> always passes.
/// </summary>
public sealed class RequireCapabilityFilter(Capability required, string errorCode) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserAccessor>();
        if (accessor.User is null)
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!accessor.User.Can(required))
            return Results.Json(new { error = errorCode }, statusCode: StatusCodes.Status403Forbidden);

        return await next(context);
    }
}

/// <summary>
/// Rejects anonymous and demo callers, but lets any real account through. Apply with
/// <see cref="RouteHandlerBuilderExtensions.RequireNonDemo"/>.
///
/// <para>
/// For endpoints that only ever act on the caller's own credentials — passkey enrollment is the
/// case this exists for. A member has to be able to enrol a passkey, otherwise passkey sign-in on
/// the native client is admin-only; the endpoints behind this filter read <c>accessor.User.Id</c>
/// and never take a user id from the request, so widening them grants a member nothing beyond
/// their own account. The demo stays out because its credentials are shared by every visitor.
/// </para>
/// </summary>
public sealed class RequireNonDemoFilter : IEndpointFilter
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
    /// <summary>
    /// Admin-only. Every pipeline and curation surface uses this — those are instance
    /// administration, not a per-person capability, so they all map to
    /// <see cref="Capability.Administer"/> rather than inventing a flag each.
    /// </summary>
    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder) =>
        builder.RequireCapability(Capability.Administer);

    /// <inheritdoc cref="RequireAdmin(RouteHandlerBuilder)"/>
    public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder builder) =>
        builder.RequireCapability(Capability.Administer);

    public static RouteHandlerBuilder RequireCapability(this RouteHandlerBuilder builder, Capability capability) =>
        builder.AddEndpointFilter(new RequireCapabilityFilter(capability, ErrorCodeFor(capability)));

    public static RouteGroupBuilder RequireCapability(this RouteGroupBuilder builder, Capability capability) =>
        builder.AddEndpointFilter(new RequireCapabilityFilter(capability, ErrorCodeFor(capability)));

    public static RouteHandlerBuilder RequireNonDemo(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RequireNonDemoFilter>();

    private static string ErrorCodeFor(Capability capability) => capability switch
    {
        Capability.Administer => "admin_required",
        _ => "capability_required",
    };
}
