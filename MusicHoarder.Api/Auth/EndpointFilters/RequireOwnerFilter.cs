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

public static class RouteHandlerBuilderExtensions
{
    public static RouteHandlerBuilder RequireOwner(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RequireOwnerFilter>();

    public static RouteGroupBuilder RequireOwner(this RouteGroupBuilder builder) =>
        builder.AddEndpointFilter<RequireOwnerFilter>();
}
