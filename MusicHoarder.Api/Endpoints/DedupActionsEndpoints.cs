using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Endpoints;

public record DedupActionRevertRequest(string Source, long BatchTicks);

/// <summary>
/// History + undo for dedup actions (artist merge, album merge, credit split, identity heal).
/// Reconstructed from the SongMetadataChanges audit log, so actions performed before this endpoint
/// existed are visible and revertible too.
/// </summary>
public static class DedupActionsEndpoints
{
    public static IEndpointRouteBuilder MapDedupActionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/library/dedup/actions", List)
            .WithName("ListDedupActions")
            .WithSummary("Recent dedup actions (merges, credit splits, heals) with per-batch summaries and revert eligibility.")
            .WithTags("Library").RequireAdmin();

        app.MapPost("/api/library/dedup/actions/revert", Revert)
            .WithName("RevertDedupAction")
            .WithSummary("Revert one dedup action: restores the audited old values, re-queues built files for re-tag, and removes the aliases a merge stored.")
            .WithTags("Library").RequireAdmin();

        return app;
    }

    internal static async Task<IResult> List(IDedupActionHistory history, CancellationToken ct)
    {
        var actions = await history.ListAsync(take: 20, ct);
        return Results.Ok(new { count = actions.Count, actions });
    }

    internal static async Task<IResult> Revert(
        DedupActionRevertRequest request, IDedupActionHistory history, ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Source) || request.BatchTicks <= 0)
            return Results.BadRequest(new { message = "source and batchTicks are required." });

        try
        {
            var result = await history.RevertAsync(currentUser.UserId, request.Source, request.BatchTicks, ct);
            if (result.ChangesReverted == 0)
                return Results.UnprocessableEntity(new { message = "Nothing left to revert in this action (already reverted?)." });
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.UnprocessableEntity(new { message = ex.Message });
        }
    }
}
