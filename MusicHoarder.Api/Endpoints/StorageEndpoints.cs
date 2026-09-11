using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Storage;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// The sidebar's storage figure and its breakdown. Both read the cached snapshot; neither measures
/// inline — a walk of the library takes minutes, and <see cref="StorageUsageBackgroundService"/> owns it.
/// </summary>
public static class StorageEndpoints
{
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/storage", GetStorageUsage).WithName("GetStorageUsage").RequireAdmin();
        app.MapPost("/storage/refresh", RefreshStorageUsage).WithName("RefreshStorageUsage").RequireAdmin();
        return app;
    }

    /// <summary>
    /// A fresh instance has no snapshot until the background service's first tick; the first caller
    /// asks for one so the popup does not sit on the startup delay.
    /// </summary>
    internal static IResult GetStorageUsage(StorageUsageSnapshotStore store)
    {
        var response = store.Get();
        if (response.Snapshot is null && !response.Computing)
        {
            store.RequestRefresh();
            response = store.Get();
        }
        return Results.Ok(response);
    }

    internal static IResult RefreshStorageUsage(StorageUsageSnapshotStore store)
    {
        if (!store.RequestRefresh())
            return Results.Conflict(new { message = "A storage measurement is already running." });
        return Results.Accepted(value: store.Get());
    }
}
