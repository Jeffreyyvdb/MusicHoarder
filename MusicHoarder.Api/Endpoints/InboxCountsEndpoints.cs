using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Counts for the Inbox's rows that would otherwise cost a full report each. The reports stay where
/// they are (<see cref="ArtistDedupEndpoints"/>, <see cref="AlbumDedupEndpoints"/>, the
/// split-albums route) for the pages that list them; this is for the badges, which are on every page.
/// </summary>
public static class InboxCountsEndpoints
{
    public static IEndpointRouteBuilder MapInboxCountsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inbox/name-counts", NameCounts)
            .WithName("GetInboxNameCounts")
            .WithSummary("How many suggestions the Inbox's Artist names queue (spelling clusters plus combined credits) and Album names queue (split albums plus duplicate-name pairs) hold, without the reports behind them.")
            .WithTags("Library").RequireAdmin();

        return app;
    }

    internal static async Task<IResult> NameCounts(
        IInboxNameCounter counter, ICurrentUserAccessor currentUser, CancellationToken ct)
    {
        var counts = await counter.CountAsync(currentUser.UserId, ct);
        return Results.Ok(counts);
    }
}
