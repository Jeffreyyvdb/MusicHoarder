namespace MusicHoarder.Api.Auth.EndpointFilters;

/// <summary>
/// Rejects a caller that does not hold <paramref name="required"/>. Authorizes against
/// <see cref="CurrentUser.Can"/>, so an <see cref="UserRole.Admin"/> always passes.
///
/// <para>
/// The one exception is the demo account reading an endpoint marked with
/// <see cref="RouteHandlerBuilderExtensions.AllowDemoRead"/> — see <see cref="DemoReadable"/>.
/// </para>
/// </summary>
public sealed class RequireCapabilityFilter(Capability required, string errorCode) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var accessor = http.RequestServices.GetRequiredService<ICurrentUserAccessor>();
        if (accessor.User is null)
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!accessor.User.Can(required) && !DemoReadable.Permits(http, accessor.User))
            return Results.Json(new { error = errorCode }, statusCode: StatusCodes.Status403Forbidden);

        return await next(context);
    }
}

/// <summary>
/// Endpoint metadata that lets the demo account through an admin gate on a read. Apply with
/// <see cref="RouteHandlerBuilderExtensions.AllowDemoRead"/>.
///
/// <para>
/// The demo account exists to show the whole product, so it keeps the admin's pages (see
/// <c>navGroupsFor</c> in the frontend's <c>#lib/nav</c>). It owns its own seeded rows, so a read
/// that goes only through the ambient per-user query filter returns only demo data. Mark a read
/// with this only when that is all it returns: nothing instance-wide (host paths, process-wide
/// pipeline or job state, catalog tables without a query filter, the instance owner's Spotify
/// account), nothing of another account's, no credentials or costs. Otherwise leave it admin-only
/// and hide the page from the demo's nav instead.
/// </para>
///
/// <para>
/// Opt-in per endpoint, so a new endpoint in an admin group stays closed to the demo until someone
/// has read it. Only GET and HEAD pass — a write on a marked route still needs the capability, on
/// top of <see cref="Middleware.DemoReadOnlyMiddleware"/>. A member gains nothing: the exception is
/// the demo account's alone.
/// </para>
/// </summary>
public sealed class DemoReadable
{
    public static readonly DemoReadable Instance = new();

    private DemoReadable() { }

    internal static bool Permits(HttpContext http, CurrentUser user) =>
        user.IsDemo
        && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
        && http.GetEndpoint()?.Metadata.GetMetadata<DemoReadable>() is not null;
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

    /// <summary>
    /// Lets the demo account read this endpoint past its admin gate. See <see cref="DemoReadable"/>
    /// for what a read must (not) return before it gets this.
    /// </summary>
    public static RouteHandlerBuilder AllowDemoRead(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(DemoReadable.Instance);

    public static RouteHandlerBuilder RequireNonDemo(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RequireNonDemoFilter>();

    private static string ErrorCodeFor(Capability capability) => capability switch
    {
        Capability.Administer => "admin_required",
        _ => "capability_required",
    };
}
